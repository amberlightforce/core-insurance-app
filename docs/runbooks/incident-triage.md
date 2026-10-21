# Incident triage by correlation id

Source: `src/CoreIns.Platform/Errors/ProblemDetailsMapper.cs`, `src/CoreIns.Platform/Http/PlatformMiddleware.cs`,
`infra/azure/modules/monitoring.bicep` (Log Analytics).

## What the caller sees
Every failure is an RFC 9457 `application/problem+json` body: `type`, `title`, `status`, `detail`, `code`
(for example `PLT-ERR-REPLAY-IN-PROGRESS`), `retryable`, `errors[]` for field errors, and `traceId` (the W3C trace id).
Ask the reporter for `traceId` and `code` (a screenshot or HAR is enough), plus the time and user.

## Correlation id (lands with NX-OT-06)
NX-OT-06 adds the header `X-Correlation-Id`: the api accepts one from the caller or generates it, echoes it on the response,
and writes it on log lines. Until OT-06 is merged, use `traceId` from the problem body (or the `traceparent` header) the same
way. Once it has landed, prefer `X-Correlation-Id` and update this page.

## Steps
1. Get the id from the report (response header or problem body).
2. Find the logs. Log Analytics: `ContainerAppConsoleLogs_CL | where Log_s contains "<id>" | order by TimeGenerated asc`.
   Live: `az containerapp logs show -n api -g $RG --follow` (use `-n worker` for async work).
3. Read the sequence: request, handler, exception. Map `code` to its definition in
   `src/CoreIns.Platform.Contracts/Generated/PlatformErrorCodes.cs` (and the module's error catalogue).
4. 4xx is a user or data problem; `detail` and `errors[]` explain it. For 5xx continue below.
5. Async symptoms (document not issued, posting missing): check `plt.outbox_dead_letter`, see
   [dead-letter-replay.md](dead-letter-replay.md); `plt.event_archive` holds the event envelope.
6. Platform checks: `/health/live`, `/health/ready`; `az containerapp revision list -n api -g $RG` for a bad rollout, see
   [deploy-rollback.md](deploy-rollback.md); a migration that just ran, see [migration-failure.md](migration-failure.md).
7. Record id, time, `code`, root cause and fix. Never copy personal data (AFM, IBAN, names) into tickets.
