begin;
create table public.rc_authority_deliveries (
  session_id uuid not null references public.rc_sessions(id) on delete cascade,
  message_id text not null check(length(message_id) between 1 and 128), connection_id text not null check(length(connection_id) between 1 and 128),
  user_id uuid not null references auth.users(id), generation bigint not null check(generation>0),
  revision bigint not null check(revision>=0), clock_epoch text not null check(length(clock_epoch) between 1 and 128),
  sent_at timestamptz not null, received_at timestamptz,
  result text not null check(result in ('pending','received','stateApplied','rejected')),
  round_trip_ms double precision check(round_trip_ms>=0 and round_trip_ms<86400000),
  primary key(session_id,message_id,connection_id)
);
create index rc_authority_deliveries_recent on public.rc_authority_deliveries(session_id,sent_at desc);
alter table public.rc_authority_deliveries enable row level security;
revoke all on public.rc_authority_deliveries from public,anon,authenticated;
grant select on public.rc_authority_deliveries to service_role;
create function public.record_rc_authority_delivery(p_session_id uuid,p_delivery jsonb)
returns void language plpgsql security definer set search_path=pg_catalog,public as $$
begin
  if not exists(select 1 from public.rc_sessions where id=p_session_id and authority_mode='relay-v3') then
    raise exception 'relay_authority_mode_required';
  end if;
  if jsonb_typeof(p_delivery) is distinct from 'object' or octet_length(p_delivery::text)>4096 then
    raise exception 'invalid_delivery_record';
  end if;
  insert into public.rc_authority_deliveries(session_id,message_id,connection_id,user_id,
    generation,revision,clock_epoch,sent_at,received_at,result,round_trip_ms)
  values(p_session_id,p_delivery->>'messageId',p_delivery->>'connectionId',(p_delivery->>'userId')::uuid,
    (p_delivery->>'generation')::bigint,(p_delivery->>'revision')::bigint,p_delivery->>'clockEpoch',
    (p_delivery->>'sentAt')::timestamptz,(p_delivery->>'receivedAt')::timestamptz,
    p_delivery->>'result',(p_delivery->>'roundTripMs')::double precision)
  on conflict(session_id,message_id,connection_id) do update set
    received_at=excluded.received_at,result=excluded.result,round_trip_ms=excluded.round_trip_ms
  where rc_authority_deliveries.result='pending' and excluded.result<>'pending'
    and rc_authority_deliveries.user_id=excluded.user_id
    and rc_authority_deliveries.generation=excluded.generation
    and rc_authority_deliveries.revision=excluded.revision
    and rc_authority_deliveries.clock_epoch=excluded.clock_epoch;
end;
$$;
revoke execute on function public.record_rc_authority_delivery(uuid,jsonb) from public,anon,authenticated;
grant execute on function public.record_rc_authority_delivery(uuid,jsonb) to service_role;
commit;
