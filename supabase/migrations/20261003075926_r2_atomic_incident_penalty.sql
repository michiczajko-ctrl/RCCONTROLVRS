begin;
alter function public.compare_exchange_rc_authority(uuid,uuid,boolean,bigint,bigint,text,jsonb)
 rename to compare_exchange_rc_authority_penalty_core;
revoke execute on function public.compare_exchange_rc_authority_penalty_core(uuid,uuid,boolean,bigint,bigint,text,jsonb)
 from public,anon,authenticated,service_role;
create function public.compare_exchange_rc_authority(p_session_id uuid,p_actor uuid,p_system boolean,
 p_expected_generation bigint,p_expected_revision bigint,p_expected_clock_epoch text,p_document jsonb)
returns boolean language plpgsql security definer set search_path=pg_catalog,public as $$
declare linked jsonb; item jsonb; context jsonb; current_revision bigint; applied record; committed boolean;
begin
 perform 1 from public.rc_sessions where id=p_session_id for update;
 select coalesce(jsonb_agg(p.value),'[]'::jsonb) into linked
 from jsonb_array_elements(coalesce(nullif(p_document->'penalties','null'::jsonb),'[]'::jsonb)) p(value)
 where p.value->'payload'->'incidentCommit' is not null and p.value->'payload'->'incidentCommit'<>'null'::jsonb
  and not exists(select 1 from public.rc_authority_penalties stored
   where stored.session_id=p_session_id and stored.penalty_id=(p.value->'payload'->>'id')::uuid);
 if jsonb_array_length(linked)>1 then raise exception 'one_incident_penalty_per_operation_required'; end if;
 if jsonb_array_length(linked)>0 then
  if p_system or not exists(select 1 from public.rc_session_participants p where p.session_id=p_session_id
    and p.user_id=p_actor and p.disconnected_at is null and (p.connection_role='host'
     or (p.connection_role in ('operator','steward') and (p.operator_permissions & 2)=2))) then
   return false;
  end if;
  insert into public.rc_incident_state(session_id) values(p_session_id) on conflict do nothing;
  select revision into current_revision from public.rc_incident_state where session_id=p_session_id for update;
  item:=linked->0; context:=item->'payload'->'incidentCommit';
  if coalesce(item->'payload'->>'penaltyType','') not in ('DriveThrough','StopAndGo','Disqualification',
   'TimePenalty5s','TimePenalty10s','TimePenalty30s','StopAndGo5s','StopAndGo10s')
   or jsonb_typeof(context) is distinct from 'object' or context->>'caseId' is distinct from item->'payload'->>'incidentId'
   or context->>'caseId' is null or char_length(context->>'caseId') not between 1 and 80
   or coalesce((context->>'expectedRevision')::bigint,-1)<>current_revision
   or not exists(select 1 from public.rc_incident_cases where session_id=p_session_id and case_id=context->>'caseId'
     and coalesce(report->>'issuedPenaltyId','')='') then return false; end if;
 end if;
 committed:=public.compare_exchange_rc_authority_penalty_core(p_session_id,p_actor,p_system,
  p_expected_generation,p_expected_revision,p_expected_clock_epoch,p_document);
 if committed and jsonb_array_length(linked)>0 then
  select * into applied from public.apply_rc_incident_case_core(p_session_id,p_actor,(item->'payload'->>'id')::uuid,
   (p_document->'state'->>'generation')::bigint,current_revision,context->>'caseId','decision',
   jsonb_build_object('caseStatus','Closed','decision','Penalty','stewardNote',coalesce(context->>'stewardNote',''),
    'assignedSteward',coalesce(context->>'assignedSteward',''),'responseToDriver',coalesce(context->>'responseToDriver','')));
  if applied.result<>'applied' then raise exception 'atomic_incident_penalty_conflict'; end if;
  update public.rc_incident_cases set report=report || jsonb_build_object('issuedPenaltyId',item->'payload'->>'id')
   where session_id=p_session_id and case_id=context->>'caseId';
 end if;
 return committed;
end; $$;
revoke execute on function public.compare_exchange_rc_authority(uuid,uuid,boolean,bigint,bigint,text,jsonb)
 from public,anon,authenticated;
grant execute on function public.compare_exchange_rc_authority(uuid,uuid,boolean,bigint,bigint,text,jsonb) to service_role;
create function public.rc_authority_schema_version() returns text language sql security definer
 set search_path=pg_catalog,public as $$ select 'r2-atomic-incident-penalty-v1'::text; $$;
revoke execute on function public.rc_authority_schema_version() from public,anon,authenticated;
grant execute on function public.rc_authority_schema_version() to service_role;
commit;
