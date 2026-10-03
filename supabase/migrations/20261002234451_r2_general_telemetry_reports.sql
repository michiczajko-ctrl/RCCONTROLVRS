begin;
-- Report ingestion is a server operation, independent of an operator's flag-control lease.
-- It updates telemetry fields only: steward decisions, assignments and history survive.
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
    on conflict(session_id,case_id) do update set report=excluded.report,updated_at=now();
  update public.rc_incident_state s set revision=s.revision+1 where s.session_id=p_session_id returning s.revision into incident_revision;
  return query select 'applied'::text,authority.generation,incident_revision,next_report;
end;
$$;
revoke execute on function public.apply_rc_rule_report(uuid,text,jsonb) from public,anon,authenticated;
grant execute on function public.apply_rc_rule_report(uuid,text,jsonb) to service_role;
commit;

