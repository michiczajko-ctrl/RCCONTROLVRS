begin;
create table public.rc_telemetry_evidence (
  session_id uuid not null references public.rc_sessions(id) on delete cascade,
  incident_id text not null check(length(incident_id) between 1 and 80),
  manifest jsonb not null check(jsonb_typeof(manifest)='object'),
  samples jsonb not null check(jsonb_typeof(samples)='array' and jsonb_array_length(samples)<=160),
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  exported_at timestamptz, primary key(session_id,incident_id)
);
alter table public.rc_telemetry_evidence enable row level security;
revoke all on public.rc_telemetry_evidence from public,anon,authenticated;
grant select on public.rc_telemetry_evidence to service_role;
create function public.write_rc_telemetry_evidence(p_session_id uuid,p_clock_epoch text,p_clip jsonb)
returns void language plpgsql security definer set search_path=pg_catalog,public as $$
declare selected_manifest jsonb:=p_clip->'manifest'; selected_samples jsonb:=p_clip->'samples'; current_epoch text;
begin
  perform 1 from public.rc_sessions where id=p_session_id and authority_mode='relay-v3' for update;
  if not found then raise exception 'relay_authority_mode_required'; end if;
  select clock_epoch into current_epoch from public.rc_session_authority where session_id=p_session_id for update;
  if current_epoch is distinct from p_clock_epoch then raise exception 'authority_clock_epoch_changed'; end if;
  if octet_length(p_clip::text)>1500000 or jsonb_typeof(selected_manifest) is distinct from 'object'
    or jsonb_typeof(selected_samples) is distinct from 'array'
    or jsonb_array_length(selected_samples)>160
    or selected_manifest->>'sessionId' is distinct from p_session_id::text
    or selected_manifest->>'id' is distinct from selected_manifest->>'incidentId'
    or coalesce(selected_manifest->>'state','') not in ('collecting','complete','partial')
    or coalesce(selected_manifest->>'checksum','') !~ '^[A-F0-9]{64}$'
    or coalesce((selected_manifest->>'sampleCount')::integer,-1)<>jsonb_array_length(selected_samples) then
    raise exception 'invalid_telemetry_evidence';
  end if;
  insert into public.rc_telemetry_evidence(session_id,incident_id,manifest,samples)
  values(p_session_id,selected_manifest->>'incidentId',selected_manifest,selected_samples)
  on conflict(session_id,incident_id) do update set manifest=excluded.manifest,samples=excluded.samples,updated_at=now()
  where rc_telemetry_evidence.manifest->>'state'='collecting' and excluded.manifest->>'state'<>'collecting'
    and rc_telemetry_evidence.manifest->>'gameEpoch'=excluded.manifest->>'gameEpoch'
    and rc_telemetry_evidence.manifest->>'sourceEpoch'=excluded.manifest->>'sourceEpoch';
end;
$$;
create function public.read_rc_telemetry_evidence(p_session_id uuid,p_incident_id text,p_offset integer,p_request_id uuid)
returns jsonb language plpgsql security definer set search_path=pg_catalog,public as $$
declare row_data public.rc_telemetry_evidence%rowtype; page jsonb;
begin
  if p_offset is null or p_offset<0 or p_offset>160 or length(coalesce(p_incident_id,'')) not between 1 and 80 then
    raise exception 'invalid_evidence_page';
  end if;
  select * into row_data from public.rc_telemetry_evidence where session_id=p_session_id and incident_id=p_incident_id;
  if not found then return jsonb_build_object('requestId',p_request_id,'samples','[]'::jsonb,'error','Evidence unavailable.'); end if;
  select coalesce(jsonb_agg(items.value order by items.ordinality),'[]'::jsonb) into page
  from jsonb_array_elements(row_data.samples) with ordinality items(value,ordinality)
  where items.ordinality>p_offset and items.ordinality<=p_offset+25;
  return jsonb_build_object('requestId',p_request_id,'manifest',row_data.manifest,'samples',page,
    'nextOffset',case when p_offset+25<jsonb_array_length(row_data.samples) then p_offset+25 else null end);
end;
$$;
create function public.interrupt_rc_telemetry_evidence(p_session_id uuid,p_clock_epoch text)
returns void language plpgsql security definer set search_path=pg_catalog,public as $$
declare current_epoch text;
begin
  select clock_epoch into current_epoch from public.rc_session_authority where session_id=p_session_id for update;
  if current_epoch is distinct from p_clock_epoch then raise exception 'authority_clock_epoch_changed'; end if;
  update public.rc_telemetry_evidence set manifest=manifest || jsonb_build_object('state','partial',
    'qualityReasons',coalesce(manifest->'qualityReasons','[]'::jsonb) || jsonb_build_array('Relay restarted')),updated_at=now()
  where session_id=p_session_id and manifest->>'state'='collecting';
end;
$$;
revoke execute on function public.interrupt_rc_telemetry_evidence(uuid,text) from public,anon,authenticated;
grant execute on function public.interrupt_rc_telemetry_evidence(uuid,text) to service_role;
revoke execute on function public.write_rc_telemetry_evidence(uuid,text,jsonb),
  public.read_rc_telemetry_evidence(uuid,text,integer,uuid) from public,anon,authenticated;
grant execute on function public.write_rc_telemetry_evidence(uuid,text,jsonb),
  public.read_rc_telemetry_evidence(uuid,text,integer,uuid) to service_role;
commit;
