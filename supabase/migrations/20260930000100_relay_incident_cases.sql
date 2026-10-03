begin;

-- Case state has its own revision: a flag or pit-lane operation must not make
-- an otherwise current steward decision stale.
create table public.rc_incident_state (
  session_id uuid primary key references public.rc_sessions(id) on delete cascade,
  revision bigint not null default 0 check (revision >= 0)
);

create table public.rc_incident_cases (
  session_id uuid not null references public.rc_sessions(id) on delete cascade,
  case_id text not null check (char_length(case_id) between 1 and 80),
  report jsonb not null check (jsonb_typeof(report) = 'object'),
  updated_at timestamptz not null default now(),
  primary key (session_id, case_id)
);

create table public.rc_incident_operations (
  session_id uuid not null references public.rc_sessions(id) on delete cascade,
  operation_id uuid not null,
  revision bigint not null,
  case_id text not null,
  actor_user_id uuid not null references auth.users(id),
  primary key (session_id, operation_id)
);

create index rc_incident_cases_updated_idx on public.rc_incident_cases(session_id, updated_at desc);

alter table public.rc_incident_state enable row level security;
alter table public.rc_incident_cases enable row level security;
alter table public.rc_incident_operations enable row level security;
revoke all on public.rc_incident_state, public.rc_incident_cases, public.rc_incident_operations
  from public, anon, authenticated;
grant all on public.rc_incident_state, public.rc_incident_cases, public.rc_incident_operations to service_role;

create or replace function public.read_rc_incidents(p_session_id uuid)
returns table(generation bigint, revision bigint, reports jsonb)
language sql security definer
set search_path = pg_catalog, public
as $$
  select state.generation, coalesce(incidents.revision, 0),
    coalesce((select jsonb_agg(c.report order by c.updated_at, c.case_id)
      from public.rc_incident_cases c where c.session_id = p_session_id), '[]'::jsonb)
  from public.rc_session_state state
  left join public.rc_incident_state incidents on incidents.session_id = state.session_id
  where state.session_id = p_session_id;
$$;

create or replace function public.apply_rc_incident_case(
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
    on conflict (session_id, case_id) do update set report = excluded.report, updated_at = now();
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

revoke all on function public.read_rc_incidents(uuid),
  public.apply_rc_incident_case(uuid,uuid,uuid,bigint,bigint,text,text,jsonb)
  from public, anon, authenticated;
grant execute on function public.read_rc_incidents(uuid),
  public.apply_rc_incident_case(uuid,uuid,uuid,bigint,bigint,text,text,jsonb)
  to service_role;

commit;
