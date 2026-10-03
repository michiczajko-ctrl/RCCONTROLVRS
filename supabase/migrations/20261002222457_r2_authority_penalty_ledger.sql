begin;
create table public.rc_authority_penalties (
  session_id uuid not null references public.rc_sessions(id) on delete cascade,
  penalty_id uuid not null, account_user_id uuid not null references auth.users(id),
  issued_by uuid not null references auth.users(id), created_at timestamptz not null,
  payload jsonb not null check(jsonb_typeof(payload)='object'), primary key(session_id,penalty_id)
);
create index rc_authority_penalties_account_page on public.rc_authority_penalties
  (session_id,account_user_id,created_at desc,penalty_id desc);
alter table public.rc_authority_penalties enable row level security;
revoke all on public.rc_authority_penalties from public,anon,authenticated;
grant select on public.rc_authority_penalties to service_role;
insert into public.rc_authority_penalties(session_id,penalty_id,account_user_id,issued_by,created_at,payload)
select a.session_id,(penalty->'payload'->>'id')::uuid,(penalty->'payload'->>'accountId')::uuid,
  (penalty->>'issuedBy')::uuid,(penalty->'payload'->>'createdAtUtc')::timestamptz,penalty->'payload'
from public.rc_session_authority a cross join lateral
  jsonb_array_elements(coalesce(nullif(a.document->'penalties','null'::jsonb),'[]'::jsonb)) items(penalty)
on conflict do nothing;
with caches as (
  select a.session_id,jsonb_agg(items.value order by items.ordinality) as recent
  from public.rc_session_authority a cross join lateral
    jsonb_array_elements(coalesce(nullif(a.document->'penalties','null'::jsonb),'[]'::jsonb)) with ordinality items(value,ordinality)
  where items.ordinality>jsonb_array_length(coalesce(nullif(a.document->'penalties','null'::jsonb),'[]'::jsonb))-16 group by a.session_id
)
update public.rc_session_authority a set document=jsonb_set(a.document,'{penalties}',caches.recent)
from caches where a.session_id=caches.session_id;

alter function public.compare_exchange_rc_authority(uuid,uuid,boolean,bigint,bigint,text,jsonb)
  rename to compare_exchange_rc_authority_operation_core;
revoke execute on function public.compare_exchange_rc_authority_operation_core(uuid,uuid,boolean,bigint,bigint,text,jsonb)
  from public,anon,authenticated,service_role;
create function public.compare_exchange_rc_authority(p_session_id uuid,p_actor uuid,p_system boolean,
  p_expected_generation bigint,p_expected_revision bigint,p_expected_clock_epoch text,p_document jsonb)
returns boolean language plpgsql security definer set search_path=pg_catalog,public as $$
declare penalties jsonb:=coalesce(nullif(p_document->'penalties','null'::jsonb),'[]'::jsonb); committed boolean;
begin
  if jsonb_typeof(penalties) is distinct from 'array' or jsonb_array_length(penalties)>16 then
    raise exception 'invalid_penalty_cache';
  end if;
  committed:=public.compare_exchange_rc_authority_operation_core(p_session_id,p_actor,p_system,
    p_expected_generation,p_expected_revision,p_expected_clock_epoch,p_document);
  if committed then
    insert into public.rc_authority_penalties(session_id,penalty_id,account_user_id,issued_by,created_at,payload)
    select p_session_id,(penalty->'payload'->>'id')::uuid,(penalty->'payload'->>'accountId')::uuid,
      (penalty->>'issuedBy')::uuid,(penalty->'payload'->>'createdAtUtc')::timestamptz,penalty->'payload'
    from jsonb_array_elements(penalties) items(penalty)
    on conflict do nothing;
  end if;
  return committed;
end;
$$;
revoke execute on function public.compare_exchange_rc_authority(uuid,uuid,boolean,bigint,bigint,text,jsonb)
  from public,anon,authenticated;
grant execute on function public.compare_exchange_rc_authority(uuid,uuid,boolean,bigint,bigint,text,jsonb) to service_role;
commit;
