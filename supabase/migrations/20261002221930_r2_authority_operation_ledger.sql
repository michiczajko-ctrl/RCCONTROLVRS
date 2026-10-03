begin;
create table public.rc_authority_operations (
  session_id uuid not null references public.rc_sessions(id) on delete cascade,
  operation_id uuid not null, generation bigint not null, revision bigint not null,
  actor_user_id uuid references auth.users(id), created_at timestamptz not null default now(),
  primary key(session_id,operation_id)
);
alter table public.rc_authority_operations enable row level security;
revoke all on public.rc_authority_operations from public,anon,authenticated;
grant select on public.rc_authority_operations to service_role;
insert into public.rc_authority_operations(session_id,operation_id,generation,revision)
select a.session_id, operation_id::uuid,a.generation,a.revision from public.rc_session_authority a
  cross join lateral jsonb_array_elements_text(coalesce(a.document->'operationIds','[]'::jsonb)) ids(operation_id)
on conflict do nothing;

with caches as (
  select a.session_id,jsonb_agg(ids.value order by ids.ordinality) as recent
  from public.rc_session_authority a
  cross join lateral jsonb_array_elements(coalesce(a.document->'operationIds','[]'::jsonb)) with ordinality ids(value,ordinality)
  where ids.ordinality>jsonb_array_length(a.document->'operationIds')-256 group by a.session_id
)
update public.rc_session_authority a set document=jsonb_set(a.document,'{operationIds}',caches.recent)
from caches where a.session_id=caches.session_id;

alter function public.compare_exchange_rc_authority(uuid,uuid,boolean,bigint,bigint,text,jsonb)
  rename to compare_exchange_rc_authority_mode_core;
revoke execute on function public.compare_exchange_rc_authority_mode_core(uuid,uuid,boolean,bigint,bigint,text,jsonb)
  from public,anon,authenticated,service_role;
create function public.compare_exchange_rc_authority(p_session_id uuid,p_actor uuid,p_system boolean,
  p_expected_generation bigint,p_expected_revision bigint,p_expected_clock_epoch text,p_document jsonb)
returns boolean language plpgsql security definer set search_path=pg_catalog,public as $$
declare old_ids jsonb; committed boolean;
begin
  perform 1 from public.rc_sessions where id=p_session_id for update;
  select document->'operationIds' into old_ids from public.rc_session_authority where session_id=p_session_id;
  if jsonb_typeof(p_document->'operationIds') is distinct from 'array'
    or jsonb_array_length(p_document->'operationIds')>256 then raise exception 'invalid_operation_cache'; end if;
  committed := public.compare_exchange_rc_authority_mode_core(p_session_id,p_actor,p_system,
    p_expected_generation,p_expected_revision,p_expected_clock_epoch,p_document);
  if committed then
    insert into public.rc_authority_operations(session_id,operation_id,generation,revision,actor_user_id)
    select p_session_id,ids.operation_id::uuid,(p_document->'state'->>'generation')::bigint,
      (p_document->'state'->>'revision')::bigint,p_actor
    from jsonb_array_elements_text(p_document->'operationIds') ids(operation_id)
    where not coalesce(old_ids,'[]'::jsonb) @> jsonb_build_array(ids.operation_id)
    on conflict do nothing;
  end if;
  return committed;
end;
$$;
revoke execute on function public.compare_exchange_rc_authority(uuid,uuid,boolean,bigint,bigint,text,jsonb)
  from public,anon,authenticated;
grant execute on function public.compare_exchange_rc_authority(uuid,uuid,boolean,bigint,bigint,text,jsonb) to service_role;
commit;
