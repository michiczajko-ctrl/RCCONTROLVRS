begin;
create table public.rc_track_definitions (
  session_id uuid not null references public.rc_sessions(id) on delete cascade,
  checksum text not null check(checksum ~ '^[A-F0-9]{64}$'),
  definition jsonb not null check(jsonb_typeof(definition)='object' and octet_length(definition::text)<=150000),
  created_at timestamptz not null default now(), primary key(session_id,checksum)
);
alter table public.rc_track_definitions enable row level security;
revoke all on public.rc_track_definitions from public,anon,authenticated;
grant select on public.rc_track_definitions to service_role;
create function public.write_rc_track_definition(p_session_id uuid,p_clock_epoch text,p_checksum text,p_definition jsonb)
returns void language plpgsql security definer set search_path=pg_catalog,public as $$
declare authority public.rc_session_authority%rowtype; old_definition jsonb;
begin
  if jsonb_typeof(p_definition) is distinct from 'object' or octet_length(p_definition::text)>150000
    or p_checksum !~ '^[A-F0-9]{64}$' or p_definition->>'verified' is distinct from 'true'
    or jsonb_typeof(p_definition->'spline') is distinct from 'array'
    or jsonb_array_length(p_definition->'spline') not between 20 and 1000 then
    raise exception 'invalid_track_definition';
  end if;
  perform 1 from public.rc_sessions where id=p_session_id and authority_mode='relay-v3' for update;
  if not found then raise exception 'relay_authority_mode_required'; end if;
  select a.* into authority from public.rc_session_authority a where a.session_id=p_session_id for update;
  if not found or authority.clock_epoch is distinct from p_clock_epoch then raise exception 'authority_clock_epoch_changed'; end if;
  select definition into old_definition from public.rc_track_definitions where session_id=p_session_id and checksum=p_checksum;
  if old_definition is not null and old_definition is distinct from p_definition then raise exception 'track_asset_is_immutable'; end if;
  insert into public.rc_track_definitions(session_id,checksum,definition) values(p_session_id,p_checksum,p_definition) on conflict do nothing;
end;
$$;
create function public.read_rc_track_definition(p_session_id uuid,p_checksum text,p_offset integer,p_request_id uuid)
returns jsonb language plpgsql security definer set search_path=pg_catalog,public as $$
declare selected jsonb; points jsonb; total integer;
begin
  if p_offset not between 0 and 1000 or p_checksum !~ '^[A-F0-9]{64}$' then raise exception 'invalid_track_request'; end if;
  select t.definition into selected from public.rc_track_definitions t
    join public.rc_session_authority a on a.session_id=t.session_id
    where t.session_id=p_session_id and t.checksum=p_checksum
      and (a.document->'state'->'trackDefinition'->>'checksum'=p_checksum
        or exists(select 1 from public.rc_telemetry_evidence e where e.session_id=t.session_id
          and e.manifest->'trackDefinition'->>'checksum'=p_checksum));
  if selected is null then return jsonb_build_object('requestId',p_request_id,'checksum',p_checksum,'error','Track profile unavailable.'); end if;
  total:=jsonb_array_length(selected->'spline');
  select coalesce(jsonb_agg(item.value order by item.ordinality),'[]'::jsonb) into points
    from jsonb_array_elements(selected->'spline') with ordinality item(value,ordinality)
    where item.ordinality>p_offset and item.ordinality<=p_offset+100;
  return jsonb_build_object('requestId',p_request_id,'checksum',p_checksum,
    'definition',selected||jsonb_build_object('spline',points),
    'nextOffset',case when p_offset+100<total then p_offset+100 else null end);
end;
$$;
revoke execute on function public.write_rc_track_definition(uuid,text,text,jsonb) from public,anon,authenticated;
revoke execute on function public.read_rc_track_definition(uuid,text,integer,uuid) from public,anon,authenticated;
grant execute on function public.write_rc_track_definition(uuid,text,text,jsonb) to service_role;
grant execute on function public.read_rc_track_definition(uuid,text,integer,uuid) to service_role;
commit;
