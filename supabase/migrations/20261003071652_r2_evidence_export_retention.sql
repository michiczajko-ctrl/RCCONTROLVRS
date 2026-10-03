begin;
alter table public.rc_telemetry_evidence add column exported_checksum text,
 add column exported_by uuid references auth.users(id);
create function public.record_rc_evidence_export(p_session_id uuid,p_actor uuid,p_incident_id text,p_checksum text)
returns boolean language plpgsql security definer set search_path=pg_catalog,public as $$
begin
 if not exists(select 1 from public.rc_session_participants where session_id=p_session_id and user_id=p_actor
   and disconnected_at is null and (connection_role='host' or (connection_role in ('operator','steward') and (operator_permissions & 2)=2))) then
  raise exception 'incident_permission_required';
 end if;
 if length(coalesce(p_incident_id,'')) not between 1 and 80 or coalesce(p_checksum,'') !~ '^[A-F0-9]{64}$' then
  raise exception 'invalid_evidence_export';
 end if;
 update public.rc_telemetry_evidence set exported_at=now(),exported_checksum=p_checksum,exported_by=p_actor
  where session_id=p_session_id and incident_id=p_incident_id and manifest->>'checksum'=p_checksum
   and manifest->>'state' in ('complete','partial');
 return found;
end; $$;
create table public.rc_evidence_retention_log (
 session_id uuid not null references public.rc_sessions(id) on delete cascade,incident_id text not null,
 checksum text not null,exported_at timestamptz not null,deleted_at timestamptz not null default now(),
 primary key(session_id,incident_id)
);
alter table public.rc_evidence_retention_log enable row level security;
revoke all on public.rc_evidence_retention_log from public,anon,authenticated;
grant select on public.rc_evidence_retention_log to service_role;
-- Only an explicit backend maintenance call runs retention. Unknown stop times are retained.
create function public.purge_rc_exported_evidence() returns integer language plpgsql security definer
 set search_path=pg_catalog,public as $$
declare candidate record; removed integer:=0;
begin
 for candidate in
  select e.session_id,e.incident_id,e.manifest->>'checksum' checksum,e.exported_at
  from public.rc_telemetry_evidence e join public.rc_sessions s on s.id=e.session_id
   join public.rc_incident_cases c on c.session_id=e.session_id and c.case_id=e.incident_id
  where s.status in ('stopped','closed') and s.stopped_at < now()-interval '30 days'
   and c.report->>'caseStatus'='Closed' and e.manifest->>'state' in ('complete','partial')
   and e.exported_at is not null and e.exported_checksum=e.manifest->>'checksum'
  order by s.stopped_at,e.session_id,e.incident_id limit 100 for update of s,c,e skip locked
 loop
  insert into public.rc_evidence_retention_log(session_id,incident_id,checksum,exported_at)
   values(candidate.session_id,candidate.incident_id,candidate.checksum,candidate.exported_at) on conflict do nothing;
  delete from public.rc_telemetry_evidence where session_id=candidate.session_id and incident_id=candidate.incident_id;
  removed:=removed+1;
 end loop;
 return removed;
end; $$;
revoke execute on function public.record_rc_evidence_export(uuid,uuid,text,text),public.purge_rc_exported_evidence()
 from public,anon,authenticated;
grant execute on function public.record_rc_evidence_export(uuid,uuid,text,text),public.purge_rc_exported_evidence() to service_role;
create index rc_evidence_exported_retention_idx on public.rc_telemetry_evidence(session_id,incident_id) where exported_at is not null;
create index rc_sessions_retention_stop_idx on public.rc_sessions(stopped_at,id) where status in ('stopped','closed');
commit;
