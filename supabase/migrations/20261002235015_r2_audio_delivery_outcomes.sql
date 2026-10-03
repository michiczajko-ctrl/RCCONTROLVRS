begin;
alter table public.rc_authority_deliveries
  add column audio_status text check(audio_status in ('queued','started','completed','muted','failed','cancelled','expired')),
  add column audio_reason text check(char_length(audio_reason)<=300);

-- Client playback outcomes are diagnostics, not proof that a driver heard the speaker.
-- Delivery and audio advance independently; late dispatch rows never undo either.
create or replace function public.record_rc_authority_delivery(p_session_id uuid,p_delivery jsonb)
returns void language plpgsql security definer set search_path=pg_catalog,public as $$
begin
  if not exists(select 1 from public.rc_sessions where id=p_session_id and authority_mode='relay-v3') then
    raise exception 'relay_authority_mode_required';
  end if;
  if jsonb_typeof(p_delivery) is distinct from 'object' or octet_length(p_delivery::text)>4096 then
    raise exception 'invalid_delivery_record';
  end if;
  insert into public.rc_authority_deliveries(session_id,message_id,connection_id,user_id,
    generation,revision,clock_epoch,sent_at,received_at,result,round_trip_ms,audio_status,audio_reason)
  values(p_session_id,p_delivery->>'messageId',p_delivery->>'connectionId',(p_delivery->>'userId')::uuid,
    (p_delivery->>'generation')::bigint,(p_delivery->>'revision')::bigint,p_delivery->>'clockEpoch',
    (p_delivery->>'sentAt')::timestamptz,(p_delivery->>'receivedAt')::timestamptz,
    p_delivery->>'result',(p_delivery->>'roundTripMs')::double precision,p_delivery->>'audioStatus',p_delivery->>'audioReason')
  on conflict(session_id,message_id,connection_id) do update set
    received_at=case when rc_authority_deliveries.result='pending' then excluded.received_at else rc_authority_deliveries.received_at end,
    result=case when rc_authority_deliveries.result='pending' then excluded.result else rc_authority_deliveries.result end,
    round_trip_ms=case when rc_authority_deliveries.result='pending' then excluded.round_trip_ms else rc_authority_deliveries.round_trip_ms end,
    audio_status=case when excluded.audio_status is not null and
      (rc_authority_deliveries.audio_status is null or rc_authority_deliveries.audio_status='queued'
        or (rc_authority_deliveries.audio_status='started' and excluded.audio_status<>'queued'))
      then excluded.audio_status else rc_authority_deliveries.audio_status end,
    audio_reason=case when excluded.audio_status is not null and
      (rc_authority_deliveries.audio_status is null or rc_authority_deliveries.audio_status='queued'
        or (rc_authority_deliveries.audio_status='started' and excluded.audio_status<>'queued'))
      then excluded.audio_reason else rc_authority_deliveries.audio_reason end
  where rc_authority_deliveries.user_id=excluded.user_id
    and rc_authority_deliveries.generation=excluded.generation
    and rc_authority_deliveries.revision=excluded.revision
    and rc_authority_deliveries.clock_epoch=excluded.clock_epoch;
end;
$$;
revoke execute on function public.record_rc_authority_delivery(uuid,jsonb) from public,anon,authenticated;
grant execute on function public.record_rc_authority_delivery(uuid,jsonb) to service_role;
commit;
