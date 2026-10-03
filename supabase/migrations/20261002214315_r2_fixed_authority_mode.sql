begin;

-- Existing sessions remain legacy. A new session explicitly chooses its contract.
alter table public.rc_sessions add column authority_mode text not null default 'legacy'
  check (authority_mode in ('legacy', 'relay-v3'));
create function public.guard_rc_authority_mode() returns trigger
language plpgsql set search_path = pg_catalog, public as $$
begin
  if new.authority_mode is distinct from old.authority_mode then
    raise exception 'authority_mode_is_immutable';
  end if;
  return new;
end;
$$;
create trigger rc_authority_mode_immutable before update of authority_mode on public.rc_sessions
for each row execute function public.guard_rc_authority_mode();
revoke execute on function public.guard_rc_authority_mode() from public, anon, authenticated;

create function public.start_rc_authority_session(p_session_id uuid, p_actor uuid) returns void
language plpgsql security definer set search_path = pg_catalog, public as $$
begin
  perform 1 from public.rc_sessions where id = p_session_id and created_by = p_actor
    and authority_mode = 'relay-v3' and status in ('scheduled','live') and expires_at > now() for update;
  if not found then raise exception 'authority_session_start_denied'; end if;
  update public.rc_sessions set status = 'live' where id = p_session_id;
  insert into public.rc_session_participants(session_id,user_id,connection_role,operator_permissions,disconnected_at)
    values (p_session_id,p_actor,'host',7,null)
    on conflict(session_id,user_id) do update set connection_role='host',operator_permissions=7,disconnected_at=null;
end;
$$;
revoke execute on function public.start_rc_authority_session(uuid, uuid) from public, anon, authenticated;
grant execute on function public.start_rc_authority_session(uuid, uuid) to service_role;

create table public.rc_authority_operator_approvals (
  session_id uuid not null references public.rc_sessions(id) on delete cascade,
  user_id uuid not null references auth.users(id), connection_role text not null,
  permissions integer not null check (permissions between 1 and 7),
  approved_by uuid not null references auth.users(id), updated_at timestamptz not null default now(),
  primary key(session_id,user_id), check(connection_role in ('operator','steward','observer'))
);
alter table public.rc_authority_operator_approvals enable row level security;
revoke all on public.rc_authority_operator_approvals from public,anon,authenticated;
grant select,insert,update,delete on public.rc_authority_operator_approvals to service_role;
create function public.set_rc_authority_operator(p_session_id uuid,p_actor uuid,p_target uuid,
  p_role text,p_permissions integer,p_approved boolean) returns void
language plpgsql security definer set search_path = pg_catalog,public as $$
declare creator uuid; maximum integer; target_role text;
begin
  select created_by into creator from public.rc_sessions where id=p_session_id
    and authority_mode='relay-v3' and status='live' and expires_at>now() for update;
  if not found then raise exception 'authority_session_required'; end if;
  if p_actor is distinct from creator and not exists(select 1 from public.rc_session_authority
    where session_id=p_session_id and controller_user_id=p_actor and lease_expires_at>now()) then
    raise exception 'authority_approval_actor_required';
  end if;
  select operator_permissions,connection_role into maximum,target_role from public.rc_session_participants
    where session_id=p_session_id and user_id=p_target;
  if p_target is null or p_target=creator or maximum is null or p_role is null or p_permissions is null
    or p_approved is null or target_role is distinct from p_role or p_role not in ('operator','steward','observer')
    or p_permissions not between 1 and 7 or (p_permissions & maximum) <> p_permissions
    or (p_role='observer' and p_permissions<>1) or (p_role='steward' and (p_permissions & 4)<>0) then
    raise exception 'invalid_authority_operator_permissions';
  end if;
  if p_approved then
    insert into public.rc_authority_operator_approvals(session_id,user_id,connection_role,permissions,approved_by)
      values(p_session_id,p_target,p_role,p_permissions,p_actor)
    on conflict(session_id,user_id) do update set connection_role=excluded.connection_role,
      permissions=excluded.permissions,approved_by=excluded.approved_by,updated_at=now();
  else delete from public.rc_authority_operator_approvals where session_id=p_session_id and user_id=p_target;
  end if;
end;
$$;
revoke execute on function public.set_rc_authority_operator(uuid,uuid,uuid,text,integer,boolean)
  from public,anon,authenticated;
grant execute on function public.set_rc_authority_operator(uuid,uuid,uuid,text,integer,boolean) to service_role;

alter function public.compare_exchange_rc_authority(uuid, uuid, boolean, bigint, bigint, text, jsonb)
  rename to compare_exchange_rc_authority_core;
revoke execute on function public.compare_exchange_rc_authority_core(uuid, uuid, boolean, bigint, bigint, text, jsonb)
  from public, anon, authenticated, service_role;
create function public.compare_exchange_rc_authority(
  p_session_id uuid, p_actor uuid, p_system boolean, p_expected_generation bigint,
  p_expected_revision bigint, p_expected_clock_epoch text, p_document jsonb
) returns boolean language plpgsql security definer set search_path = pg_catalog, public as $$
declare selected_mode text; committed boolean;
begin
  select authority_mode into selected_mode from public.rc_sessions where id = p_session_id for update;
  if selected_mode is distinct from 'relay-v3' then raise exception 'relay_authority_mode_required'; end if;
  committed := public.compare_exchange_rc_authority_core(p_session_id, p_actor, p_system,
    p_expected_generation, p_expected_revision, p_expected_clock_epoch, p_document);
  if committed then
    -- Incident storage uses this generation too. Both projections commit atomically.
    update public.rc_session_state set
      generation = (p_document->'state'->>'generation')::bigint,
      revision = (p_document->'state'->>'revision')::bigint,
      owner_user_id = coalesce((p_document->'state'->>'controllerId')::uuid, owner_user_id),
      snapshot = jsonb_build_object('authorityMode', 'relay-v3'), updated_at = now()
    where session_id = p_session_id;
    if not found then raise exception 'session_state_not_found'; end if;
  end if;
  return committed;
end;
$$;
revoke execute on function public.compare_exchange_rc_authority(uuid, uuid, boolean, bigint, bigint, text, jsonb)
  from public, anon, authenticated;
grant execute on function public.compare_exchange_rc_authority(uuid, uuid, boolean, bigint, bigint, text, jsonb) to service_role;

alter function public.take_rc_session_ownership(uuid, uuid, bigint, text) rename to take_rc_session_ownership_legacy;
revoke execute on function public.take_rc_session_ownership_legacy(uuid, uuid, bigint, text)
  from public, anon, authenticated, service_role;
create function public.take_rc_session_ownership(p_session_id uuid, p_actor uuid, p_expected_generation bigint, p_reason text default '')
returns table(generation bigint, revision bigint)
language plpgsql security definer set search_path = pg_catalog, public as $$
begin
  perform 1 from public.rc_sessions where id = p_session_id and authority_mode = 'legacy' for update;
  if not found then raise exception 'legacy_authority_mode_required'; end if;
  return query select * from public.take_rc_session_ownership_legacy(p_session_id, p_actor, p_expected_generation, p_reason);
end;
$$;
revoke execute on function public.take_rc_session_ownership(uuid, uuid, bigint, text) from public, anon, authenticated;
grant execute on function public.take_rc_session_ownership(uuid, uuid, bigint, text) to service_role;

alter function public.apply_rc_session_operation(uuid, uuid, uuid, bigint, bigint, text, jsonb)
  rename to apply_rc_session_operation_legacy;
revoke execute on function public.apply_rc_session_operation_legacy(uuid, uuid, uuid, bigint, bigint, text, jsonb)
  from public, anon, authenticated, service_role;
create function public.apply_rc_session_operation(p_session_id uuid, p_actor uuid, p_operation_id uuid,
  p_expected_generation bigint, p_expected_revision bigint, p_operation_kind text, p_snapshot jsonb)
returns table(result text, generation bigint, revision bigint)
language plpgsql security definer set search_path = pg_catalog, public as $$
begin
  perform 1 from public.rc_sessions where id = p_session_id and authority_mode = 'legacy' for update;
  if not found then raise exception 'legacy_authority_mode_required'; end if;
  return query select * from public.apply_rc_session_operation_legacy(p_session_id, p_actor, p_operation_id,
    p_expected_generation, p_expected_revision, p_operation_kind, p_snapshot);
end;
$$;
revoke execute on function public.apply_rc_session_operation(uuid, uuid, uuid, bigint, bigint, text, jsonb)
  from public, anon, authenticated;
grant execute on function public.apply_rc_session_operation(uuid, uuid, uuid, bigint, bigint, text, jsonb) to service_role;

alter function public.apply_rc_incident_case(uuid, uuid, uuid, bigint, bigint, text, text, jsonb)
  rename to apply_rc_incident_case_core;
revoke execute on function public.apply_rc_incident_case_core(uuid, uuid, uuid, bigint, bigint, text, text, jsonb)
  from public, anon, authenticated, service_role;
create function public.apply_rc_incident_case(p_session_id uuid, p_actor uuid, p_operation_id uuid,
  p_expected_generation bigint, p_expected_revision bigint, p_case_id text, p_kind text, p_payload jsonb)
returns table(result text, generation bigint, revision bigint, report jsonb)
language plpgsql security definer set search_path = pg_catalog, public as $$
declare selected_mode text;
begin
  -- Same lock order as authority CAS. Steward review remains independent of controller health.
  select authority_mode into selected_mode from public.rc_sessions where id = p_session_id for update;
  if selected_mode = 'relay-v3' and p_kind = 'replace' and not exists (
    select 1 from public.rc_session_authority where session_id = p_session_id
      and controller_user_id = p_actor and lease_expires_at > now()
  ) then raise exception 'live_authority_controller_required'; end if;
  return query select * from public.apply_rc_incident_case_core(p_session_id, p_actor, p_operation_id,
    p_expected_generation, p_expected_revision, p_case_id, p_kind, p_payload);
end;
$$;
revoke execute on function public.apply_rc_incident_case(uuid, uuid, uuid, bigint, bigint, text, text, jsonb)
  from public, anon, authenticated;
grant execute on function public.apply_rc_incident_case(uuid, uuid, uuid, bigint, bigint, text, text, jsonb) to service_role;
commit;
