# R2 deployment and acceptance runbook

Scope: LMU, Paul Ricard A1, existing GT3/HY qualification format. This is an acceptance procedure, not evidence that deployment or rehearsal has occurred.

## Local verification

`scripts/Build-R2Candidate.ps1` runs the local regressions and builds a unique `publish/R2-CANDIDATE-*` prerelease package. HOST and CLIENT are self-contained Windows x64 apps; Relay is framework-dependent and needs .NET 9 ASP.NET Core. `candidate.json` records SHA256 hashes and explicitly marks live-event readiness false. Use the newest verified candidate, not a superseded snapshot. This script leaves the source release version and existing official packages unchanged.

From the repository root:

```powershell
dotnet build VRSRaceControl.sln -c Release --no-restore --verbosity quiet
dotnet test tests/VRS.RaceControl.Tests --no-restore --logger 'trx;LogFileName=r2-current.trx'
node tests/VRS.RaceControl.Database.Tests/authority-schema-test.mjs
```

The database harness runs the actual SQL locally in PostgreSQL/PGlite. It does not migrate Supabase. The TCP proxy tests delays and stalls; they do not prove IP packet-loss behavior. Runtime restart tests do not replace a killed-process rehearsal.

## Staging rollout

1. Record the current deployed Relay/HOST/CLIENT build identifiers and the rollback package. Export active cases and evidence before changing infrastructure. Do not replace a running live-event authority.
2. Apply the repository's additive migrations in timestamp order to staging. Include all R2 migrations through `20261003075926_r2_atomic_incident_penalty.sql`, not just the authority base migration. Keep the service-role key exclusively in Relay/server secrets.
3. Deploy the matching Relay with both durable stores configured. Initially keep `VRS_RELAY_AUTHORITY_ENABLED=false` and `VRS_EVIDENCE_RETENTION_ENABLED=false`.
4. Enable authority only in staging. Public root discovery must advertise `session-state-v3`, `scheduled-panel-audio-v1`, `raceguard-evidence-v1`, `shared-track-definitions-v1`, `telemetry-v2`, `telemetry-detail-v1`, and `atomic-incident-penalty-v1`. It must report `authoritySchemaReady=true`, `durableSessionStore=supabase`, `durableIncidentStore=configured`, and at least 262144 `maxMessageBytes`. `/health` alone does not establish these properties.
5. Create a new v3 session from the matching HOST. HOST preflight rejects a missing contract/store/budget; Relay rejects a missing schema. An older installation uses a separately created legacy session; authority mode never changes mid-session.
6. Verify authenticated admission and approvals using actual staging accounts. An unapproved operator cannot control, review private evidence, or publish fleet telemetry. An ordinary driver cannot read another account's private penalties or the operator fleet feed.
7. Keep retention disabled during acceptance. Explicit export registration requires a successful local evidence save and matching server checksum. Retention additionally requires a known session stop date older than 30 days and a closed case; unknown stop dates are deliberately retained.

## Three-station rehearsal

Use three physical operator computers, the actual LMU build, the target hosting topology and at least 40 drivers or the league's greater supported grid size. Duration: two hours minimum. Record UTC boundaries, build version/MVID/SHA256, game version, session IDs, controller generations, revisions, socket close codes, measured clock uncertainty and voice outcomes. Do not record login tokens or service keys.

Perform the following in order:

1. Join all three approved operators and drivers; verify identical state/revision and synchronisation quality. Transfer A→B→C→A repeatedly. No transfer closes unrelated sockets, resets flags, changes qualification deadlines or restarts the telemetry provider.
2. Kill the historical main HOST while B controls. B and C remain operational. Kill the controller, wait for lease expiry, and submit competing takeover requests: one commits, the other resynchronises. Returning A does not steal control.
3. Exercise all six GT3/HY qualification commands. Hear the intended cue on actual clients, including a driver with no class assignment. Verify 15-minute deadlines and automatic END, including controller loss/reconnect. END preserves RED/FCY/SC/READY priority.
4. Issue FCY with separate FCY/pit limits of 60 km/h. Confirm effective activation, 10-second grace, tolerance and two-second continuous evidence. One episode creates one Pending report; no automatic penalty. Confirm pit cars and unknown pit status do not become FCY offenders.
5. Review a report from B while A controls. Exercise Warning and Dismiss separately from live penalties. For a penalty, deliberately submit a stale review: no penalty commits. For a fresh review, the penalty ledger and closed case decision commit together. Reconnect/retry does not sanction twice. Driver/history payloads do not contain private steward notes.
6. Issue READY, ARM, CANCEL and RED. READY has no visible or recorded countdown. Only a committed GREEN timestamp triggers GREEN; transfer/reconnect does not redraw the random wait. Excessive uncertainty prevents ARM. Relay restart requires re-arming instead of firing an overdue GREEN.
7. Remove the telemetry transport, stop/restart LMU, remove the player vehicle and repeat in spectator mode. Check new-sample age and fleet count, not just shared-memory existence. Rules stop counting on gaps; recovery does not reconstruct an unobserved violation. Record which inputs of remote cars are actually available.
8. Record a clean Paul Ricard A1 lap; inspect and verify the measured profile before publishing it. With LMU's absent layout field, publish the explicit current-layout confirmation and check matching source/game epochs and observed track length. Confirm shared map/evidence applicability disappears after a source/game change until confirmed again. Check start/finish wrapping, sectors, pit lane, named segments, world marker alignment and map zoom/pan on all operators. A schematic or unverified profile does not pass calibration.
9. Check incident A/B timeline from −5 to +2 seconds. Compare speed, available inputs, spatial separation and along-track separation against LMU evidence. Gaps and unavailable fields stay explicit. Export, reopen/checksum-validate, and verify export registration. Do not claim 20 Hz unless observed timestamps establish it.
10. Test asymmetric latency, 50/150/300/600 ms RTT, jitter and transport packet loss using an actual network fault facility. Add UI stalls. Healthy links target ≤50 ms uncertainty and ≤100 ms display skew; degraded links must be marked rather than show false current state.
11. Restart the actual Relay process, then interrupt its database connection. Existing state stays visible as stale/read-only; no mutation claims success without durable commit. Restore the database, reconnect and compare flags, qualification deadlines, case decisions, penalty IDs and evidence. New join tokens are obtained as needed.
12. Interrupt P2/P3 audio with P0/P1, disconnect/rejoin, and exhaust a slow recipient. No old cue replays from snapshots, unrelated playback is not cancelled and one failing recipient does not stop the others. CLIENT-reported `completed` still requires a human audibility check.

## Release and rollback gate

Keep the development source version unchanged until the minor-release compatibility decision and acceptance are complete. The existing `Build-Release.ps1` builds the source version's official folder and can overwrite its EXEs; do not use it to replace the existing 1.1.2 package with an unaccepted development build.

Release as a matched HOST/CLIENT/Relay set only after the rehearsal has no unresolved P0/P1, an operator signs off on the actual map/audio/evidence, and the results include build hashes. Migration files and enabled capabilities belong in the release manifest. Rule output remains reports, never automatic sanctions or account/economy deductions.

Rollback preserves additive tables/columns and evidence. Disable admission/creation of new v3 sessions before rollback. Finish or explicitly stop/export an active v3 session; never run the legacy authority over its database state. Restore the known-good Relay and matched applications for newly created legacy sessions. Confirm manual race-control procedures before the event.
