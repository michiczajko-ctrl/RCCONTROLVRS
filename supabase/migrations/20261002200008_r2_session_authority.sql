begin;

-- Separate versioned authority; legacy sessions keep their existing contract.
create table public.rc_session_authority (
  session_id uuid primary key references public.rc_sessions(id) on delete cascade,
  generation bigint not null check (generation > 0),
  revision bigint not null check (revision >= 0),
  clock_epoch text not null check (length(clock_epoch) between 1 and 128),
  controller_user_id uuid references auth.users(id) on delete restrict,
  lease_expires_at timestamptz,
  document jsonb not null check (jsonb_typeof(document) = 'object'),
  updated_at timestamptz not null default now(),
  check ((controller_user_id is null) = (lease_expires_at is null))
);
alter table public.rc_session_authority enable row level security;
revoke all on public.rc_session_authority from public, anon, authenticated;
grant select, insert, update, delete on public.rc_session_authority to service_role;

create function public.compare_exchange_rc_authority(
  p_session_id uuid, p_actor uuid, p_system boolean,
  p_expected_generation bigint, p_expected_revision bigint,
  p_expected_clock_epoch text, p_document jsonb
) returns boolean
language plpgsql security definer
set search_path = pg_catalog, public
as $$
declare
  old_state public.rc_session_authority%rowtype;
  session_creator uuid;
  next_state jsonb := p_document->'state';
  next_generation bigint := (next_state->>'generation')::bigint;
  next_revision bigint := (next_state->>'revision')::bigint;
  next_controller uuid := (next_state->>'controllerId')::uuid;
  next_lease timestamptz := (next_state->>'leaseExpiresAt')::timestamptz;
begin
  -- Lock the parent too: concurrent initialization is serialized.
  select s.created_by into session_creator from public.rc_sessions s
    where s.id = p_session_id and s.status = 'live' and s.expires_at > now() for update;
  if not found then raise exception 'session_not_live'; end if;
  if jsonb_typeof(next_state) <> 'object' or (next_state->>'sessionId')::uuid <> p_session_id
    or octet_length(p_document::text) > 1048576
    or next_generation is null or next_revision is null then
    raise exception 'invalid_authority_document';
  end if;
  select a.* into old_state from public.rc_session_authority a
    where a.session_id = p_session_id for update;
  if not found then
    if p_expected_generation is not null or p_expected_revision is not null then return false; end if;
    if p_system or p_actor is distinct from session_creator or next_controller is distinct from p_actor
      or next_generation <> 1 or next_revision <> 0 then raise exception 'invalid_initial_authority'; end if;
  else
    if old_state.generation is distinct from p_expected_generation
      or old_state.revision is distinct from p_expected_revision
      or old_state.clock_epoch is distinct from p_expected_clock_epoch then return false; end if;
    if next_generation < old_state.generation or next_generation > old_state.generation + 1
      or next_revision < old_state.revision or next_revision > old_state.revision + 1 then
      raise exception 'invalid_authority_sequence';
    end if;
    if not p_system then
      if old_state.lease_expires_at > now() then
        if p_actor is distinct from old_state.controller_user_id then raise exception 'controller_required'; end if;
      elsif p_actor is distinct from next_controller or next_generation <> old_state.generation + 1 then
        raise exception 'expired_lease';
      end if;
    end if;
    if next_controller is distinct from old_state.controller_user_id
      and next_generation <> old_state.generation + 1 then raise exception 'generation_required'; end if;
    -- Same revision is reserved for lease renewal; it cannot alter domain state.
    if next_revision = old_state.revision and (
      next_generation <> old_state.generation
      or next_controller is distinct from old_state.controller_user_id
      or (p_document #- '{state,leaseExpiresAt}') is distinct from (old_state.document #- '{state,leaseExpiresAt}')
    ) then raise exception 'invalid_lease_renewal'; end if;
  end if;
  if not p_system and not exists (
    select 1 from public.rc_session_participants p where p.session_id = p_session_id
      and p.user_id = p_actor and p.disconnected_at is null
      and (p.connection_role = 'host' or (p.connection_role = 'operator' and (p.operator_permissions & 4) = 4))
  ) then raise exception 'control_permission_required'; end if;
  if next_controller is not null and not p_system and not exists (
    select 1 from public.rc_session_participants p where p.session_id = p_session_id
      and p.user_id = next_controller and p.disconnected_at is null
      and (p.connection_role = 'host' or (p.connection_role = 'operator' and (p.operator_permissions & 4) = 4))
  ) then raise exception 'target_control_permission_required'; end if;
  if next_lease is not null and next_lease > now() + interval '10 seconds' then raise exception 'lease_too_long'; end if;
  insert into public.rc_session_authority(session_id, generation, revision, clock_epoch,
    controller_user_id, lease_expires_at, document)
  values (p_session_id, next_generation, next_revision, next_state->>'clockEpoch', next_controller, next_lease, p_document)
  on conflict(session_id) do update set generation = excluded.generation, revision = excluded.revision,
    clock_epoch = excluded.clock_epoch, controller_user_id = excluded.controller_user_id,
    lease_expires_at = excluded.lease_expires_at, document = excluded.document, updated_at = now();
  return true;
end;
$$;
revoke execute on function public.compare_exchange_rc_authority(uuid, uuid, boolean, bigint, bigint, text, jsonb)
  from public, anon, authenticated;
grant execute on function public.compare_exchange_rc_authority(uuid, uuid, boolean, bigint, bigint, text, jsonb) to service_role;

commit;
