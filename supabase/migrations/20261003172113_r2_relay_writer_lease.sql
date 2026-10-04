begin;
-- Opt-in per session; once present, an expired writer remains fenced until a new Relay acquires it.
create table public.rc_relay_writer_leases (
 session_id uuid primary key references public.rc_sessions(id) on delete cascade,
 clock_epoch text not null check (length(clock_epoch) between 1 and 128),
 expires_at timestamptz not null
);
alter table public.rc_relay_writer_leases enable row level security;
revoke all on public.rc_relay_writer_leases from public,anon,authenticated,service_role;
grant select on public.rc_relay_writer_leases to service_role;

create function public.lease_rc_relay_writer(p_session_id uuid,p_clock_epoch text,p_acquire boolean)
returns boolean language plpgsql security definer set search_path=pg_catalog,public as $$
declare held public.rc_relay_writer_leases%rowtype; lease_time timestamptz;
begin
 if p_clock_epoch is null or length(p_clock_epoch) not between 1 and 128 or p_acquire is null then
  raise exception 'invalid_relay_writer_request';
 end if;
 perform 1 from public.rc_sessions where id=p_session_id and status='live' and expires_at>clock_timestamp() for update;
 if not found then return false; end if;
 select * into held from public.rc_relay_writer_leases where session_id=p_session_id for update;
 lease_time:=clock_timestamp();
 if not found then
  if not p_acquire then return false; end if;
  insert into public.rc_relay_writer_leases values(p_session_id,p_clock_epoch,lease_time+interval '20 seconds');
  return true;
 end if;
 if held.clock_epoch=p_clock_epoch and held.expires_at>lease_time then
  update public.rc_relay_writer_leases set expires_at=lease_time+interval '20 seconds' where session_id=p_session_id;
  return true;
 end if;
 if not p_acquire or held.clock_epoch=p_clock_epoch or held.expires_at>lease_time then return false; end if;
 update public.rc_relay_writer_leases set clock_epoch=p_clock_epoch,expires_at=lease_time+interval '20 seconds'
  where session_id=p_session_id;
 return true;
end; $$;
revoke execute on function public.lease_rc_relay_writer(uuid,text,boolean) from public,anon,authenticated;
grant execute on function public.lease_rc_relay_writer(uuid,text,boolean) to service_role;

create function public.fence_rc_relay_writer() returns trigger
language plpgsql security definer set search_path=pg_catalog,public as $$
declare held public.rc_relay_writer_leases%rowtype;
begin
 select * into held from public.rc_relay_writer_leases where session_id=new.session_id for update;
 if found and (held.clock_epoch<>new.clock_epoch or held.expires_at<=clock_timestamp()) then
  raise exception 'relay_writer_fenced';
 end if;
 return new;
end; $$;
revoke execute on function public.fence_rc_relay_writer() from public,anon,authenticated,service_role;
create trigger rc_authority_relay_writer_fence before insert or update on public.rc_session_authority
 for each row execute function public.fence_rc_relay_writer();
-- The same fence also covers telemetry writes, independently of operator ownership.
create function public.assert_rc_relay_writer(p_session_id uuid,p_clock_epoch text) returns void
language plpgsql security definer set search_path=pg_catalog,public as $$
declare held public.rc_relay_writer_leases%rowtype;
begin
 perform 1 from public.rc_sessions where id=p_session_id for update;
 select * into held from public.rc_relay_writer_leases where session_id=p_session_id for update;
 if found and (held.clock_epoch is distinct from p_clock_epoch or held.expires_at<=clock_timestamp()) then
  raise exception 'relay_writer_fenced';
 end if;
end; $$;
revoke execute on function public.assert_rc_relay_writer(uuid,text) from public,anon,authenticated,service_role;

alter function public.apply_rc_rule_report(uuid,text,jsonb) rename to apply_rc_rule_report_unleased_core;
revoke execute on function public.apply_rc_rule_report_unleased_core(uuid,text,jsonb) from public,anon,authenticated,service_role;
create function public.apply_rc_rule_report(p_session_id uuid,p_clock_epoch text,p_report jsonb)
returns table(result text,generation bigint,revision bigint,report jsonb)
language plpgsql security definer set search_path=pg_catalog,public as $$
begin
 perform public.assert_rc_relay_writer(p_session_id,p_clock_epoch);
 return query select * from public.apply_rc_rule_report_unleased_core(p_session_id,p_clock_epoch,p_report);
end; $$;
revoke execute on function public.apply_rc_rule_report(uuid,text,jsonb) from public,anon,authenticated;
grant execute on function public.apply_rc_rule_report(uuid,text,jsonb) to service_role;

alter function public.write_rc_telemetry_evidence(uuid,text,jsonb) rename to write_rc_telemetry_evidence_unleased_core;
revoke execute on function public.write_rc_telemetry_evidence_unleased_core(uuid,text,jsonb) from public,anon,authenticated,service_role;
create function public.write_rc_telemetry_evidence(p_session_id uuid,p_clock_epoch text,p_clip jsonb)
returns void language plpgsql security definer set search_path=pg_catalog,public as $$
begin
 perform public.assert_rc_relay_writer(p_session_id,p_clock_epoch);
 perform public.write_rc_telemetry_evidence_unleased_core(p_session_id,p_clock_epoch,p_clip);
end; $$;
revoke execute on function public.write_rc_telemetry_evidence(uuid,text,jsonb) from public,anon,authenticated;
grant execute on function public.write_rc_telemetry_evidence(uuid,text,jsonb) to service_role;

alter function public.write_rc_track_definition(uuid,text,text,jsonb) rename to write_rc_track_definition_unleased_core;
revoke execute on function public.write_rc_track_definition_unleased_core(uuid,text,text,jsonb) from public,anon,authenticated,service_role;
create function public.write_rc_track_definition(p_session_id uuid,p_clock_epoch text,p_checksum text,p_definition jsonb)
returns void language plpgsql security definer set search_path=pg_catalog,public as $$
begin
 perform public.assert_rc_relay_writer(p_session_id,p_clock_epoch);
 perform public.write_rc_track_definition_unleased_core(p_session_id,p_clock_epoch,p_checksum,p_definition);
end; $$;
revoke execute on function public.write_rc_track_definition(uuid,text,text,jsonb) from public,anon,authenticated;
grant execute on function public.write_rc_track_definition(uuid,text,text,jsonb) to service_role;
alter function public.interrupt_rc_telemetry_evidence(uuid,text) rename to interrupt_rc_telemetry_evidence_unleased_core;
revoke execute on function public.interrupt_rc_telemetry_evidence_unleased_core(uuid,text) from public,anon,authenticated,service_role;
create function public.interrupt_rc_telemetry_evidence(p_session_id uuid,p_clock_epoch text)
returns void language plpgsql security definer set search_path=pg_catalog,public as $$
begin
 perform public.assert_rc_relay_writer(p_session_id,p_clock_epoch);
 perform public.interrupt_rc_telemetry_evidence_unleased_core(p_session_id,p_clock_epoch);
end; $$;
revoke execute on function public.interrupt_rc_telemetry_evidence(uuid,text) from public,anon,authenticated;
grant execute on function public.interrupt_rc_telemetry_evidence(uuid,text) to service_role;
commit;

