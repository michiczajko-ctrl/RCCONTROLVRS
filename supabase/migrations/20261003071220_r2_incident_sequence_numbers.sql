begin;
alter table public.rc_incident_state add column next_sequence_number integer not null default 1 check(next_sequence_number > 0);
alter table public.rc_incident_cases add column sequence_number integer;
-- Preserve existing positive numbers where unique; repair missing/duplicate local numbers.
with existing as (
 select session_id,case_id,case when coalesce(report->>'sequenceNumber','') ~ '^[0-9]{1,9}$'
 then (report->>'sequenceNumber')::integer else 0 end n from public.rc_incident_cases
), ranked as (
 select *,row_number() over(partition by session_id,n order by case_id) ordinal from existing
) update public.rc_incident_cases c set sequence_number=r.n from ranked r
 where c.session_id=r.session_id and c.case_id=r.case_id and r.n>0 and r.ordinal=1;
with missing as (
 select session_id,case_id,row_number() over(partition by session_id order by updated_at,case_id) ordinal
 from public.rc_incident_cases where sequence_number is null
), maxima as (select session_id,coalesce(max(sequence_number),0) maximum from public.rc_incident_cases group by session_id)
update public.rc_incident_cases c set sequence_number=(m.maximum+x.ordinal)::integer from missing x join maxima m using(session_id)
 where c.session_id=x.session_id and c.case_id=x.case_id;
update public.rc_incident_cases set report=jsonb_set(report,'{sequenceNumber}',to_jsonb(sequence_number));
insert into public.rc_incident_state(session_id,next_sequence_number)
 select session_id,max(sequence_number)+1 from public.rc_incident_cases group by session_id
 on conflict(session_id) do update set next_sequence_number=excluded.next_sequence_number;
alter table public.rc_incident_cases alter column sequence_number set not null;
alter table public.rc_incident_cases add constraint rc_incident_sequence_positive check(sequence_number>0);
create unique index rc_incident_sequence_unique on public.rc_incident_cases(session_id,sequence_number);
create function public.assign_rc_incident_sequence() returns trigger language plpgsql security definer
 set search_path=pg_catalog,public as $$
begin
 if TG_OP='UPDATE' then
  new.sequence_number:=old.sequence_number;
  if old.report->>'issuedPenaltyId' is not null then
   new.report:=new.report || jsonb_build_object('issuedPenaltyId',old.report->>'issuedPenaltyId');
  end if;
 else
  insert into public.rc_incident_state(session_id) values(new.session_id) on conflict do nothing;
  perform 1 from public.rc_incident_state where session_id=new.session_id for update;
  select sequence_number into new.sequence_number from public.rc_incident_cases
   where session_id=new.session_id and case_id=new.case_id;
  if new.sequence_number is null then
   update public.rc_incident_state set next_sequence_number=next_sequence_number+1
    where session_id=new.session_id returning next_sequence_number-1 into new.sequence_number;
  end if;
 end if;
 new.report:=jsonb_set(new.report,'{sequenceNumber}',to_jsonb(new.sequence_number));
 return new;
end; $$;
revoke execute on function public.assign_rc_incident_sequence() from public,anon,authenticated;
create trigger rc_incident_assign_sequence before insert or update on public.rc_incident_cases
 for each row execute function public.assign_rc_incident_sequence();
create or replace function public.apply_rc_incident_case_core(
  p_session_id uuid, p_actor uuid, p_operation_id uuid,
  p_expected_generation bigint, p_expected_revision bigint,
  p_case_id text, p_kind text, p_payload jsonb
) returns table(result text, generation bigint, revision bigint, report jsonb)
language plpgsql security definer
set search_path = pg_catalog, public
as $$
declare
  state_row public.rc_session_state%rowtype;
  incident_row public.rc_incident_state%rowtype;
  old_report jsonb;
  next_report jsonb;
  decision text;
  case_status text;
  legacy_status text;
  actor_allowed boolean;
begin
  if p_operation_id is null or char_length(coalesce(p_case_id, '')) not between 1 and 80
     or p_kind not in ('replace', 'decision') or jsonb_typeof(p_payload) <> 'object'
     or octet_length(p_payload::text) > 140000 then
    raise exception 'invalid_incident_operation' using errcode = 'P0001';
  end if;
  select s.* into state_row from public.rc_session_state s
    where s.session_id = p_session_id for update;
  if not found then raise exception 'session_state_not_found' using errcode = 'P0002'; end if;
  select exists(select 1 from public.rc_session_participants p
    where p.session_id = p_session_id and p.user_id = p_actor
      and p.disconnected_at is null
      and (p.connection_role = 'host' or
        (p.connection_role in ('operator','steward') and (p.operator_permissions & 2) = 2)))
    into actor_allowed;
  if not actor_allowed then raise exception 'incident_permission_required' using errcode = 'P0003'; end if;
  if p_kind = 'replace' and state_row.owner_user_id <> p_actor then
    raise exception 'session_owner_required' using errcode = 'P0004';
  end if;

  insert into public.rc_incident_state(session_id) values (p_session_id)
    on conflict (session_id) do nothing;
  select s.* into incident_row from public.rc_incident_state s
    where s.session_id = p_session_id for update;
  if exists(select 1 from public.rc_incident_operations o
      where o.session_id = p_session_id and o.operation_id = p_operation_id) then
    select c.report into old_report from public.rc_incident_cases c
      where c.session_id = p_session_id and c.case_id = p_case_id;
    return query select 'duplicate'::text, state_row.generation, incident_row.revision, old_report;
    return;
  end if;
  if state_row.generation <> p_expected_generation
     or incident_row.revision <> p_expected_revision then
    return query select 'conflict'::text, state_row.generation, incident_row.revision, null::jsonb;
    return;
  end if;

  select c.report into old_report from public.rc_incident_cases c
    where c.session_id = p_session_id and c.case_id = p_case_id;
  if p_kind = 'replace' then
    if p_payload->>'id' <> p_case_id then
      raise exception 'incident_identity_mismatch' using errcode = 'P0005';
    end if;
    next_report := jsonb_set(p_payload, '{history}',
      coalesce(p_payload->'history', '[]'::jsonb) || jsonb_build_array(jsonb_build_object(
        'timestampUtc', now(), 'actor', p_actor::text,
        'action', case when old_report is null then 'Case created' else 'Case evidence updated' end)));
  else
    if old_report is null then
      return query select 'missing'::text, state_row.generation, incident_row.revision, null::jsonb;
      return;
    end if;
    decision := p_payload->>'decision';
    case_status := p_payload->>'caseStatus';
    if decision not in ('Pending','NoFurtherAction','Warning','Penalty')
       or case_status not in ('New','Reviewing','Closed')
       or char_length(coalesce(p_payload->>'stewardNote','')) > 1000
       or char_length(coalesce(p_payload->>'responseToDriver','')) > 600
       or char_length(coalesce(p_payload->>'assignedSteward','')) > 120 then
      raise exception 'invalid_incident_decision' using errcode = 'P0006';
    end if;
    legacy_status := case decision
      when 'NoFurtherAction' then 'NoFurtherAction'
      when 'Warning' then 'Warning'
      when 'Penalty' then 'PenaltyIssued'
      else case case_status when 'New' then 'New' when 'Reviewing' then 'UnderReview' else 'Closed' end end;
    next_report := old_report || jsonb_build_object(
      'caseStatus', case_status, 'decision', decision, 'status', legacy_status,
      'workflowVersion', 1,
      'stewardNote', coalesce(p_payload->>'stewardNote',''),
      'responseToDriver', coalesce(p_payload->>'responseToDriver',''),
      'assignedSteward', coalesce(p_payload->>'assignedSteward',''),
      'updatedAtUtc', now());
    next_report := jsonb_set(next_report, '{history}',
      coalesce(old_report->'history', '[]'::jsonb) || jsonb_build_array(jsonb_build_object(
        'timestampUtc', now(), 'actor', p_actor::text, 'action', 'Decision changed',
        'previousStatus', old_report->>'status', 'newStatus', legacy_status,
        'previousCaseStatus', old_report->>'caseStatus', 'newCaseStatus', case_status,
        'previousDecision', old_report->>'decision', 'newDecision', decision,
        'previousAssignedSteward', old_report->>'assignedSteward',
        'newAssignedSteward', coalesce(p_payload->>'assignedSteward',''),
        'note', coalesce(p_payload->>'stewardNote',''))));
  end if;
  insert into public.rc_incident_cases(session_id, case_id, report)
    values (p_session_id, p_case_id, next_report)
    on conflict (session_id, case_id) do update set report = excluded.report, updated_at = now()
    returning rc_incident_cases.report into next_report;
  update public.rc_incident_state s set revision = s.revision + 1
    where s.session_id = p_session_id returning s.revision into revision;
  insert into public.rc_incident_operations(session_id, operation_id, revision, case_id, actor_user_id)
    values (p_session_id, p_operation_id, revision, p_case_id, p_actor);
  generation := state_row.generation;
  result := 'applied';
  report := next_report;
  return next;
end;
$$;

create or replace function public.apply_rc_rule_report(p_session_id uuid,p_clock_epoch text,p_report jsonb)
returns table(result text,generation bigint,revision bigint,report jsonb)
language plpgsql security definer set search_path=pg_catalog,public as $$
declare authority public.rc_session_authority%rowtype; old_report jsonb; next_report jsonb;
  selected_rule jsonb:=coalesce(nullif(p_report->'telemetryObservation','null'::jsonb),p_report->'telemetryRule'); selected_id text:=p_report->>'id';
  old_rule jsonb; incident_revision bigint;
begin
  if jsonb_typeof(p_report) is distinct from 'object' or octet_length(p_report::text)>140000
    or char_length(coalesce(selected_id,'')) not between 1 and 80
    or jsonb_typeof(selected_rule) is distinct from 'object'
    or coalesce(selected_rule->>'ruleId','') not in ('FCY_SPEEDING','PIT_SPEEDING','IMPACT')
    or selected_rule->>'id' is distinct from selected_id
    or coalesce((selected_rule->>'revision')::bigint,0)<1
    or p_report->>'source' is distinct from 'Auto'
    or p_report->>'sessionId' is distinct from p_session_id::text then
    raise exception 'invalid_rule_report';
  end if;
  -- Consistent lock ordering with authority and steward decisions.
  perform 1 from public.rc_sessions where id=p_session_id and authority_mode='relay-v3' for update;
  if not found then raise exception 'relay_authority_mode_required'; end if;
  select a.* into authority from public.rc_session_authority a where a.session_id=p_session_id for update;
  if not found then raise exception 'authority_not_found'; end if;
  insert into public.rc_incident_state(session_id) values(p_session_id) on conflict do nothing;
  select s.revision into incident_revision from public.rc_incident_state s where s.session_id=p_session_id for update;
  if authority.clock_epoch is distinct from p_clock_epoch then
    return query select 'conflict'::text,authority.generation,incident_revision,null::jsonb; return;
  end if;
  select c.report into old_report from public.rc_incident_cases c where c.session_id=p_session_id and c.case_id=selected_id;
  if old_report is not null then
    old_rule:=coalesce(nullif(old_report->'telemetryObservation','null'::jsonb),old_report->'telemetryRule');
    if old_rule is null or old_rule->>'ruleId' is distinct from selected_rule->>'ruleId'
      or old_rule->>'gameEpoch' is distinct from selected_rule->>'gameEpoch'
      or old_rule->>'sourceEpoch' is distinct from selected_rule->>'sourceEpoch'
      or old_rule->>'periodId' is distinct from selected_rule->>'periodId'
      or old_rule->>'vehicleId' is distinct from selected_rule->>'vehicleId'
      or old_rule->'vehicleIds' is distinct from selected_rule->'vehicleIds'
      or old_rule->>'configurationVersion' is distinct from selected_rule->>'configurationVersion' then
      raise exception 'rule_episode_identity_is_immutable';
    end if;
    if (old_rule->>'revision')::bigint >= (selected_rule->>'revision')::bigint then
      return query select 'duplicate'::text,authority.generation,incident_revision,old_report; return;
    end if;
    next_report:=old_report || jsonb_build_object('telemetryRule',p_report->'telemetryRule','telemetryObservation',p_report->'telemetryObservation','description',p_report->'description',
      'lap',p_report->'lap','sector',p_report->'sector','trackPositionNormalized',p_report->'trackPositionNormalized',
      'worldPosition',p_report->'worldPosition','updatedAtUtc',now());
  else
    next_report:=p_report || jsonb_build_object('caseStatus','New','decision','Pending','status','New',
      'assignedSteward','','stewardNote','','responseToDriver','','history','[]'::jsonb);
  end if;
  insert into public.rc_incident_cases(session_id,case_id,report) values(p_session_id,selected_id,next_report)
    on conflict(session_id,case_id) do update set report=excluded.report,updated_at=now()
    returning rc_incident_cases.report into next_report;
  update public.rc_incident_state s set revision=s.revision+1 where s.session_id=p_session_id returning s.revision into incident_revision;
  return query select 'applied'::text,authority.generation,incident_revision,next_report;
end;
$$;
commit;
