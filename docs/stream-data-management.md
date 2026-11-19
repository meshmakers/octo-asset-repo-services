---
description: Stream data REST API: writes, archives, rollups, coverage and recompute jobs, row origin, validation and authorization of the stream data controller.
applies_to: **/StreamData/Controllers/**, **/ConfigureStreamDataConfiguration.cs, tests/**/StreamData/**
---

# Stream data management over REST

## 4. Stream Data Management

Stream-data **reads** (time series) go through GraphQL and need a stream-data role there
([tenant-enforcement.md](tenant-enforcement.md#authorization-stream-data-reads-staged)); the switch of that gate to `Enforce`
per environment, and what it protects and what not, is in [stream-data-read-enforce-plan.md](stream-data-read-enforce-plan.md).
The REST controller below has no time-series query endpoint.

Time-series data support (`StreamData/`):
- **StreamDataController** - REST API for stream data operations. **Tenant-scoped:**
  the whole controller moved from `api/v1/streamdata` to `{tenantId}/v1/streamdata` — the
  tenant now travels in the route (`{tenantId:tenantId}` constraint) instead of a `tenantId`
  query parameter on every action. **Write actions that change an archive or its columns use `StreamDataAdministrationPolicy`
**: scope `octo_api` plus the tenant role `StreamDataAdmin` (central `AddTenantRolePolicy`, both role claim spellings),
  the same rule as the GraphQL mutations and the Archive CK methods; a refusal is 403 with
  `{"message", "code": "StreamDataAdminRequired"}`. Covered: archive lifecycle (activate / disable / enable / retry / delete),
  rollup freeze / unfreeze / rewind / recompute / backfill-from-source, `rollups/repair`, computed columns (add / formula / remove /
  retry / repair) and `archives/{id}/columns`. Before the role check they used the scope-only `TenantAssetApiReadWritePolicy` (any
  `octo_api` token passed). **`POST enable` and `POST disable` use `TenantAdministrationPolicy`**
: scope plus the tenant role `TenantManagement`, a token without the role gets 403 with a `{"message"}` body naming the role.
  `insert` / `insertTimeRange` stay on `TenantAssetApiReadWritePolicy` (automated points: scope only; manual points: role
  check in the action). Read actions (`.../rollups`, `.../recompute-jobs`, `.../coverage`, `rollups/validate`) use
  `TenantAssetApiReadOnlyPolicy`. Tests: `UnitTests/Configuration/StreamDataAdministrationAuthorizationTests` (per endpoint
  group: scope-only refused, role allowed; a guard that every non-GET action carries the policy or is a listed exception;
  403 body with code through a TestServer pipeline). A new archive-changing endpoint must carry `StreamDataAdministrationPolicy`
  and be added to an endpoint group of that test. The `api/v1/streamdata` route no longer exists.
  - **Lifecycle and rollup endpoints are facades of the Archive methods.** `POST archives/{id}/activate`,
    `/disable`, `/enable`, `/retry`, `DELETE archives/{id}`, `POST archives/{id}/freeze`, `/unfreeze` and `/rewind` call
    `Archive.Activate-1`, `Disable-1`, `Enable-1`, `RetryActivation-1`, `DeleteArchive-1` and `RollupArchive.Freeze-1`,
    `Unfreeze-1`, `RewindWatermark-1` through `IArchiveMethodFacade` (`StreamData/Methods/ArchiveMethodFacade.cs`):
    `ICkMethodGateway` (caller tenant, declared role `StreamDataAdmin`, data permission on the archive, parameter validation),
    then `ICkMethodDispatcher` on the in-process path. The handler runs outside the request's Mongo session
    (request-session tripwire) and the pipeline writes exactly one Method audit event per call. The controller only resolves the
    tenant and keeps the one read-only pre-check its contract needs (no stream data: `400` with the message it always had);
    it holds no lifecycle rule. Routes, bodies and status codes are unchanged; REST is not deprecated and has no Sunset header.
    Mapping (`LifecycleFacadeOptions`, `CkMethodFacadeResults.ToActionResult`):

    | REST | Method | Success | Refusal |
    |---|---|---|---|
    | `POST archives/{id}/activate` | `System.StreamData/Archive.Activate-1` | `204` | `STREAMDATA_*` → `400` + engine message |
    | `POST archives/{id}/disable` | `System.StreamData/Archive.Disable-1` | `204` | as above |
    | `POST archives/{id}/enable` | `System.StreamData/Archive.Enable-1` | `204` | as above |
    | `POST archives/{id}/retry` | `System.StreamData/Archive.RetryActivation-1` | `204` | as above |
    | `DELETE archives/{id}` | `System.StreamData/Archive.DeleteArchive-1` | `204` | as above |
    | `POST archives/{id}/freeze?until=` | `System.StreamData/RollupArchive.Freeze-1` (`until`) | `204` | as above |
    | `POST archives/{id}/unfreeze?acceptGaps=` | `System.StreamData/RollupArchive.Unfreeze-1` (`acceptGaps`) | `204` | as above |
    | `POST archives/{id}/rewind?toBucketEnd=` | `System.StreamData/RollupArchive.RewindWatermark-1` (`toBucketEnd`) | `204` | as above |

    `METHOD_TARGET_NOT_FOUND` (an archive the gateway does not see) stays `400` with the engine's not-found message,
    `STREAMDATA_NOT_ENABLED` `400` with the endpoint's message. Every other platform code keeps the default of
    `CkMethodFacadeResults` as problem details: `METHOD_FORBIDDEN` `403` (a token of another tenant with reason
    `tenant-not-allowed` when the HTTP tenant gate runs in LogOnly, a caller without the role behind the REST policy, a
    data permission that refuses the archive — new, the direct call had no data-permission check), `METHOD_VALIDATION_FAILED` `400`,
    `METHOD_HANDLER_NOT_AVAILABLE` / `METHOD_TIMEOUT` `503` / `504`. With `Methods:StreamData:Enabled=false` no handler is
    hosted and the endpoints keep their direct lifecycle call; so do they in a tenant whose CK model does not declare
    the method (System.StreamData older than the methods, or not resolved: `IArchiveMethodFacade.CanServe`), where the
    gateway would answer `403` for an unknown method. That direct call (the **fallback**) runs behind
    `IArchiveMethodFallbackGuard` (see "The fallback guard" below): the same caller-tenant and data-permission
    checks as the method path, one audit event and one WARN line per call. Tests:
    `IntegrationTests/StreamData/ArchiveMethodFacadeTests` (fixture `ArchiveMethodFacadeFixture` observes the audit and the
    session tripwire). The column and computed-column endpoints are facades too (next item).
  - **Column and computed-column endpoints are facades of the Archive column methods (System.StreamData 1.28.0).**
    The six endpoints call these methods through `IArchiveMethodFacade` like the lifecycle endpoints (gateway with
    caller tenant, declared role `StreamDataAdmin` and data permission on the archive; `ICkMethodDispatcher` on the in-process
    path, outside the request's Mongo session; exactly one Method audit event per call). They run under the host's stopping
    token, as the direct call did: a client that disconnects does not cancel a queued change. Routes, query
    parameters and status codes are unchanged; REST is not deprecated and has no Sunset header. Parity table
    (`StreamDataController.InvokeColumnMethodAsync` / `InvokeColumnJobMethodAsync`, mapping `LifecycleFacadeOptions`):

    | REST | Method | Parameters | Success | Differences |
    |---|---|---|---|---|
    | `POST archives/{id}/computed-columns?name=&formula=&resultType=&indexed=` | `System.StreamData/Archive.AddComputedColumn-1` | `name`, `formula`, `resultType` (by member name, `ArchiveColumnChanges.ResultTypeOf`), `indexed` (default `true`) | `204` | none for valid input; a `resultType` number outside the enum is `400` problem details `METHOD_VALIDATION_FAILED` (field `resultType`) instead of an engine error |
    | `POST archives/{id}/columns?path=&indexed=` | `System.StreamData/Archive.AddColumn-1` | `path`, `indexed` (default `false`) | `204` | none; a path that reaches a Hidden attribute stays `400` with the engine message |
    | `DELETE archives/{id}/computed-columns/{name}` | `System.StreamData/Archive.RemoveComputedColumn-1` | `name` | `204` | none (an unknown column stays a no-op `204`) |
    | `PUT archives/{id}/computed-columns/{name}?formula=` | `System.StreamData/Archive.UpdateComputedColumnFormula-1` | `name`, `formula` | `204` | an unknown column: `400` with the engine message instead of an unhandled `500` |
    | `POST archives/{id}/computed-columns/{name}/retry` | `System.StreamData/Archive.RetryComputedColumnBackfill-1` | `name` | `200` + `RecomputeJobInfoRestDto`; `204` without a job | the job is the method's `RecomputeJobInfo` result, mapped field by field (`RecomputeJobInfoRestDto.From(RtRecomputeJobInfoRecord)`), not read again |
    | `POST archives/{id}/computed-columns/{name}/repair?from=&to=` | `System.StreamData/Archive.RepairComputedColumn-1` | `name`, `from`, `to` (an offset-free value is UTC, as before) | `200` + `RecomputeJobInfoRestDto`; `204` without a job | as retry |

    **Time limit (all six):** the methods declare `execution.timeoutSeconds: 60`. A change that takes longer on the method path
    (for example a slow CrateDB `ALTER TABLE` in `AddColumn-1`) answers `504` problem details `METHOD_TIMEOUT`, while the change
    keeps running under the host's stopping token; the direct call had no limit and answered `204` once done. The
    fallback keeps the direct call without a limit.

    Refusals: every `STREAMDATA_*` code `400` with the engine message as the body (the direct call's answer),
    `METHOD_TARGET_NOT_FOUND` `400` with the not-found message, `STREAMDATA_NOT_ENABLED` `400` with the endpoint's message;
    `METHOD_FORBIDDEN` `403` problem details (a token of another tenant with reason `tenant-not-allowed`, a caller without
    the role behind the REST policy, a data permission that refuses the archive - new, the direct call had none). The handler
    turns the engine's plain refusal of an unknown computed column into `STREAMDATA_ERROR`; the fallback's direct call does the
    same (`ArchiveColumnChanges.RefusingUnknownColumnsAsync`), so both paths answer `400`. With the switch off, or in a tenant
    whose System.StreamData is older than 1.28.0, the endpoints keep their direct call behind the fallback guard (below). The
    deprecated GraphQL column mutations are unchanged (they still call the engine directly). Data access beyond the
    target archive: none - the methods read and change only the target archive, its CrateDB table and its recompute jobs. Tests:
    `IntegrationTests/StreamData/ColumnMethodFacadeTests` (every endpoint with one audit event, the request-session tripwire,
    the Hidden attribute on both paths, refusals and the unknown column on both paths, both abuse cases on both paths, the
    switch off), `ArchiveMethodFacadeHttpTests` (the six routes through the real MVC pipeline: no role and a foreign token are
    `403` and change nothing, on the method path and the fallback), `UnitTests/StreamData/Methods/ArchiveMethodTests`
    (`ResultTypeOf`, `JobOf`).
  - **Recompute and backfill are facades of the asynchronous methods (System.StreamData 1.30.0).**
    `POST archives/{id}/recompute?from=&to=&rtIdScope=` calls `RollupArchive.Recompute-2`, `POST archives/{id}/backfill-from-source`
    calls `RollupArchive.BackfillFromSource-2`, through `IArchiveMethodFacade` like the lifecycle endpoints (gateway, declared
    role, data permission, one Method audit event with outcome `Accepted`). The method accepts the call as an operation; the
    controller then reads the operation record from the operation store (`RecomputeOperationWait`, every
    `Methods:StreamData:OperationPollInterval`, default 1 s) and answers the job from the job store with
    `RecomputeJobInfoRestDto`, exactly the shape of the direct call:

    | REST | Method | Waits until | Answer |
    |---|---|---|---|
    | `POST archives/{id}/recompute` | `System.StreamData/RollupArchive.Recompute-2` | the operation ended | `200` + the job (Completed, or Failed for a refused precondition: operation `FAILED` with `STREAMDATA_RECOMPUTE_FAILED`) |
    | `POST archives/{id}/backfill-from-source` | `System.StreamData/RollupArchive.BackfillFromSource-2` | the operation reported its queued job (`jobRtId`) or ended, at most `Methods:StreamData:BackfillQueueTimeout` (60 s) | `200` + the Pending job; `204` for an empty source; `504` problem details naming the operation after the timeout |

    The accept and the wait run under the host's stopping token, never the request : a client that disconnects
    does not stop the recompute or the backfill, which stay visible in `runtime.operations` and in `recompute-jobs`.
    **Behaviour change on an error path:** recompute/backfill-from-source on a deleted or non-rollup archive now answers 400
    (not found, the engine's "does not exist" message) instead of 200 with a Failed job: the gateway refuses the call with
    `METHOD_TARGET_NOT_FOUND` before any job exists, so no job is persisted and no operation is accepted (the method path
    refuses the call itself, it is never an accepted operation). A fallback to the direct call is not possible there: the
    gateway answers a rollup the caller has no data permission on the same way. Reads of the job and of the operation record
    that fail are retried (10 in a row end the wait).
    Refusals map as for the lifecycle endpoints (`STREAMDATA_*` `400`, `METHOD_FORBIDDEN` `403`, ...); an operation record
    that is not readable any more answers `500` problem details naming the operation. With the switch off, or in a tenant whose
    System.StreamData is older than 1.30.0, the endpoints keep their direct call, behind the fallback guard (below). Tests:
    `IntegrationTests/StreamData/RecomputeMethodFacadeTests`, `UnitTests/StreamData/Methods/RecomputeAsyncHandlerTests`.
  - **The fallback guard.** Every archive call that does not run through its CK method runs behind
    `StreamData/Methods/ArchiveMethodFallbackGuard` (`IArchiveMethodFallbackGuard`, registered with the facade, with the switch on
    and off; a host without it fails the call instead of running it unchecked). That is: the eight lifecycle and rollup
    endpoints, the six column and computed-column endpoints, `recompute` and `backfill-from-source` and the deprecated lifecycle and rollup mutations while
    `IArchiveMethodFacade.CanServe` is false, and the deprecated mutations `recomputeArchive` and `backfillRollupFromSource`
    always (they are no facade of a method; reason `not-routed`). Before the direct call it runs, in this order:
    1. the caller tenant: `CkMethodCallerTenantCheck` of octo-common-services, the rule of the gateway (step 0), including
       `TenantAuthorization:CrossTenantServiceClientIds` and the parent-tenant rule, independent of the HTTP tenant gate's mode.
       A token of another tenant is `METHOD_FORBIDDEN` / `tenant-not-allowed`;
    2. the data permission on the archive: loaded with the caller's rights under the declaring CK type (`Archive` or
       `RollupArchive`, as the facade does) and judged on its stored type with the data action the tenant's model declares for the
       method, else `Write` (`CkMethodTargetAccessChecker`). An archive the caller cannot see is `METHOD_TARGET_NOT_FOUND`, a visible
       one the caller may not change `METHOD_FORBIDDEN` / `data-permission`. The answer is `CkMethodCallChecks.RefusalOf`, the
       function the gateway uses.
    The declared role is not judged again (the REST policy and the mutations' role check refuse first), the parameters are not
    validated again (the direct call validates its input as before). A refusal answers exactly like the method path: REST
    `403` problem details (`400` with the not-found message for an archive the caller cannot see), the mutations their
    `METHOD_FORBIDDEN` error. An authorised caller gets the old answer (`204`, `400` for an engine refusal, the job). Every call
    writes **exactly one** Method audit event through `ICkMethodAuditRecorder`, also for a refusal or a fault: transport
    `fallback`, the method id the call stands for, outcome `SUCCESS`, the refusal code, the `STREAMDATA_*` code of an engine
    refusal, `CALLER_CANCELLED` or `METHOD_HANDLER_FAILED`; no handler instance. And it logs **one WARN line**
    `<operation> in tenant <tenant> ran on the fallback of <method id> (<reason>), invocation <id>: <outcome>` with the reason
    `switch-off`, `method-not-declared` or `not-routed`; never the target id, parameters, user or token. That WARN line is the
    only line at Warning or above for the call: when the engine refuses it (`400`), the endpoint logs the refusal at Debug
    with the exception type only; the target id and the engine message stay in the body of the answer. **Strict audit**
    (`Methods:Audit:FailCallOnAuditFailure`, or the tenant in `FailCallOnAuditFailureTenants`): when the audit event of a call
    that ran cannot be written, the answer is `METHOD_OUTCOME_UNKNOWN` (REST `500` problem details, the mutation's error),
    the answer of the method path's audit filter - for a call that succeeded and for an engine refusal (the method handler
    answers it as a `STREAMDATA_*` failure, which the filter does not count as "not executed"). A refusal of the guard itself
    (caller tenant, data permission) did not run and stays the refusal. There is no option to turn the guard off. Tests:
    `IntegrationTests/StreamData/ArchiveMethodFallbackGuardTests` (both abuse cases on every REST endpoint, the mutations, the
    recompute and backfill mutations, the old answers, the WARN line and no target id at Warning or above on an engine
    refusal, strict audit for an executed call and an engine refusal),
    `ArchiveMethodFacadeHttpTests` (a foreign token through the real MVC pipeline without the tenant gate).
  - **Computed columns queue their backfill.** `POST .../computed-columns` (add) and
    `PUT .../computed-columns/{name}` (formula change), like the GraphQL mutations `addComputedColumn` /
    `updateComputedColumnFormula`, only validate, provision and queue: they call the engine's
    `EnqueueAdd…` / `EnqueueUpdate…` with the host's stopping token (never the request token) and return
    with the column `Backfilling`. The backfill runs on `ComputedColumnBackfillHostedService`
    (registered in `Program.cs` via `AddComputedColumnBackfillBackgroundService`, options
    `StreamData:ComputedColumnBackfill`, slice width `StreamData:ComputedColumnBackfillSlice`), one at a
    time, and shows up as a recompute job with `computedColumnName` set. `POST .../computed-columns/{name}/retry`
    and the mutation `retryComputedColumnBackfill` re-queue a `Failed` column (400 / error for an
    `Active` one). Never call the inline `AddComputedColumnAsync` / `UpdateComputedColumnFormulaAsync`
    from a request again — they backfill in the call.
  - **Repair of an active computed column.** `POST .../computed-columns/{name}/repair?from=&to=` and the
    mutation `repairComputedColumn(rtId, name, from, to)` call `IArchiveLifecycleService.RepairComputedColumnAsync`:
    a recompute job (marker `computed-column:repair:<name>`, visible in the recompute jobs with `computedColumnName`)
    that the backfill service runs after the queued backfills. It re-evaluates only rows whose cell is NULL while an
    operand of the formula is not NULL, inside `[from, to)` (default: first row to now); the column stays `Active`.
    400 / error for a column that is not `Active`. An evaluation aborted by an exception (out of memory)
    is no longer stored as NULL: ingest fails the write, a backfill retries the page once and then fails the job with
    the time slice in the reason.
  - **Ingested column on an active archive.** `POST .../archives/{rtId}/columns?path=&indexed=` and the
    mutation `addArchiveColumn` call `IArchiveLifecycleService.AddArchiveColumnAsync` (storage first, nullable
    `ADD COLUMN`, then the definition; idempotent; 400 for a non-activated archive, a rollup or an unresolvable path).
    A blueprint update whose seed adds columns to an activated archive reports them in `archiveColumnsAdded`
    (REST result and GraphQL `BlueprintApplyResult`); rules in `octo-construction-kit-engine-mongodb/docs/streamdata-archive-concept.md` §10.
  - **Archives a blueprint seed creates** are activated by the engine on install and update, new rollups get a backfill job; the
    REST results (`BlueprintApplyResultDto`, `BlueprintUpdateResultDto`) and GraphQL `BlueprintApplyResult` carry `archiveActivations`
    (`BlueprintArchiveActivationDto`: archive, outcome `Activated|SkippedPreExisting|SkippedOptOut|SkippedStreamDataDisabled|Failed`, reason,
    backfill `NotApplicable|Queued|NothingToBackfill|NotQueued|Failed`, job id, reason). Mapping `BlueprintUpdatePreviewMapper.ToDtos`; tests
    `TenantApi/BlueprintArchiveActivationResultTests`. Rules: `octo-construction-kit-engine/docs/blueprints.md`.
  - `POST disable` is a **verified precondition, not a teardown**: 409 +
    `OperationFailedErrorDto` naming the archives while any is still `Activated` (guard in the
    engine's `TenantContext.DisableStreamDataAsync`); on success only the tenant flag is switched
    off — model, archive entities and the tables of Disabled/Failed archives stay until the tenant
    is dropped. The Studio's Tenant Features panel reflects the flag via
    `GET {tenantId}/v1/features/status` (`FeaturesController`) — the CK model stays after
    a disable, so model presence would lie. The former `GET streamdata/status` is removed.
  - `GET rollups/validate?rollupRtId=…`, `POST rollups/repair?rollupRtId=…&dryRun=true|false`, `GET rollups/repair/{requestId}`:
    operator check and repair of the rollup state after a restore (octo-cli `ValidateRollups` / `RepairRollups` / `GetRollupRepairStatus`,
    MCP `validate_rollups` / `repair_rollups` / `get_rollup_repair_status`). Validate is read only (ReadOnly policy; counts and MAX/MIN, no rows).
    Repair needs `StreamDataAdministrationPolicy` (scope plus role `StreamDataAdmin`; also for the dry run) and **defaults to a dry run** (planned steps per rollup, nothing changed). `dryRun=false` only **requests** the
    repair: `IStreamDataRepository.EnqueueRollupRepairAsync` writes a request and the call answers **202** with the request and a `Location`
    to the status resource; the host's recompute tick (`RecomputeOrchestratorHostedService` in the engine) executes it, so a long recompute is
    never bound to an HTTP request (an ingress cuts a request long before it ends). A second request while one is active returns the
    active one. The status endpoint (ReadOnly) returns `Pending|Running|Done|Failed` with the final report; unknown id = 404. No
    `rollupRtId` = every rollup. A repository without the feature answers 400. Tests: `StreamDataControllerRollupConsistencyTests`.
    A rollup the validator cannot check is an entry with `isNotChecked: true`, `notChecked: {cause, reason, isBlocking}` (NoLiveTable|NeverActivated|SourceMissing). `isBlocking` is false only for NeverActivated (informational: `isOk` stays true, the report stays ok); a blocking gap makes `isOk` false (`isBlockedByNotChecked`). The report carries `hasNotChecked` (any) and `hasBlockingNotChecked`. Fields are additive.
  - `POST archives/{archiveRtId}/insertTimeRange` (ReadWrite) — **origin, writer, reason**:
    each point may carry `origin` (`manual` | `automated`, case-insensitive; absent = automated, so existing callers are
    unchanged) and, only with `manual`, an optional `reason`. The writer of a manual row comes from the caller's token
    (`StreamDataController.CallerIdentity`: `preferred_username`, else `name`, else `ClaimTypes.Name`, else the
    subject id; `client_id` only when the token has no subject), **never** a body field;
    the platform time is set by the repository. 400 for an unknown origin (names the valid values), a reason without
    `manual`, and a manual write whose token names no identity; nothing of the batch is written. **A manual point must name
    every ingested column of the archive**, explicit `null` allowed. Before a change in engine-mongodb the upsert bound
    an unsent column as NULL and a correction of one column cleared the others; since then an unsent column keeps its stored
    value, and the rule stays because the origin is stored per row: a manual point marks the whole row manual and protects it,
    so it has to state every value it protects. Otherwise 400 listing the missing columns, nothing written. **A ranking column
    (column key of the archive's `ConflictPrecedence`; `$Origin` is no column) cannot be null** (engine-mongodb, decided
    2026-11-09): an explicit `null` on one answers 400 naming the point and the key, on `insert` and `insertTimeRange`,
    automated and manual (`ArchivePointValidation.CheckColumns`; the engine refuses it too, `ArchiveRankingColumnValidator`).
    Left out, a ranking column keeps its stored value and the guard compares that value. **Ranking columns are exempt from the
    manual full-row rule** (decided 2026-11-09): a manual point may leave them out and they keep their stored value, so a
    correction of a row whose ranking column is stored as NULL is possible. **Same point checks
    as the raw `insert`**, for automated and manual points, each a 400 naming the point and the field, nothing of the batch
    written: missing or sub-millisecond `from`/`to` (UTC normalization like the raw timestamp, `ArchivePointValidation.NormalizeTimestamp`),
    and an attribute that is no ingested column, a computed column, or a second key for the same column
    (`ArchivePointValidation.CheckColumns`, keys matched like the storage column). Before, the repository dropped unknown and computed
    keys silently and the call answered 200. Pipeline nodes write through the repository and are not affected. Without a readable
    archive snapshot the column checks are skipped. Tests:
    `StreamDataControllerInsertTimeRangeTests`, `IntegrationTests/StreamData/StreamDataInsertTimeRangeRestTests`. SDK: `IStreamDataServicesClient.InsertTimeRangeAsync` (`TimeRangePointDto`).
    **Write result:** answers **200** with `StreamDataWriteResultRestDto` (`stored`, `skipped`,
    `skippedByReason` — `LowerPrecedence` / `ManualValueProtected` / `CkTypeMismatch` —, `skippedRows` up to 1,000);
    an empty batch stays 204. A manual row the archive did not store (`ManualStreamDataNotAppliedException`) answers
    **409** `{ message, result }` — never success. Callers that ignore the body behave as before.
  - `POST archives/{archiveRtId}/insert` (ReadWrite) — **raw-archive writes and corrections**: body
    `InsertRawPointRestDto[]` (`rtId`, `ckTypeId`, `timestamp`, `rtWellKnownName?`, `attributes`, `origin?`, `reason?`), handed
    to the existing `IStreamDataRepository.InsertAsync` (no engine interface change). **Row identity:** archive + `rtId` +
    `timestamp` (UTC, millisecond precision), the CK type is fixed per archive (raw PK `(timestamp, rtid, cktypeid)`); a
    timestamp without offset is read as UTC, one with offset converted, sub-millisecond precision answers 400. **Protection is
    per row**: the origin is stored once per row, so a manual correction of one attribute also protects the other attributes
    of that row against automated re-deliveries. Origin, reason and writer as `insertTimeRange` (shared `TryParseOrigin`,
    `CallerIdentity`; writer and write time never from the body). Checks before anything is written, each a 400 naming the
    point (index) and the field (`ArchivePointValidation`, against the archive snapshot): invalid `rtId`, missing or
    sub-millisecond timestamp, invalid origin, reason without `manual`, manual write with a token without writer, `ckTypeId`
    not the archive's target (shared `CkTypeMismatchMessage`), an attribute that is no ingested column or a computed column, two
    keys for the same column (keys are matched like the storage column: segments concatenated, case-insensitive), a time-range
    or rollup archive, and a **manual** point that does not name every ingested column (explicit
    `null` allowed; the message lists the missing columns), because a manual point protects the whole row (see `insertTimeRange`). A value that does not fit its column type is refused by the repository's value check (engine-mongodb,
    `ArgumentException`) and answered 400 like the time-range path. Answers 200 `StreamDataWriteResultRestDto`
    (a skipped raw row has `from` = timestamp, `to` = null), 204 for an empty batch, 409 `ManualWriteNotStoredRestDto` when a
    manual row was not stored, 403 via the policy and for a manual point without the role `StreamDataAdmin` (see below). Without a readable archive snapshot (stream data off, unknown archive) the
    column checks are skipped and the repository's own error answers 400. Tests: `UnitTests/StreamData/StreamDataControllerInsertRawTests`, `IntegrationTests/StreamData/StreamDataInsertRawRestTests` (real CrateDB: manual correction, automated re-delivery skipped with `ManualValueProtected`, whole row stays manual). SDK:
    `IStreamDataServicesClient.InsertAsync` (`RawPointDto`).
  - **Malformed requests and incomplete writes of both inserts.** An `archiveRtId` that is not a runtime id,
    a point `rtId` that is not one and a `null` array element answer 400 (`CheckInsertRequest`, before anything is read). When
    CrateDB does not store every row, also after one retry (`StreamDataWriteIncompleteException`), the answer is 503 with a
    `ProblemDetails` body (`archiveRtId`, `rowsInAffectedWrite`, `rowsNotStored` - counts of the one failed write
    statement, not of the request, whose other points may be stored - generic detail "reload before retrying") and `Retry-After`
    (`WriteIncompleteRetryAfterSeconds`); the qualified table and the CrateDB text are logged only, never returned. Tests:
    `StreamDataControllerWriteIncompleteTests`, the malformed-request cases of `StreamDataControllerInsertRawTests` /
    `StreamDataControllerInsertTimeRangeTests`.
  - **Manual writes need the role `StreamDataAdmin`** on both `insert` and `insertTimeRange`: a batch with at
    least one point of origin `manual` is written only when the verified caller holds the tenant role `StreamDataAdmin`
    (`CommonConstants.StreamDataAdminRole`, both role claim spellings via `TenantRoleAuthorizationHandler.HasTenantRole`;
    `StreamDataReader`/`StreamDataWriter` are not enough). Otherwise the whole batch answers **403** `ForbiddenErrorDto` naming
    the role, nothing written (`StreamDataController.RefuseManualWriteWithoutRole`, after the per-point 400 checks). Automated
    points keep the scope-only check of `TenantAssetApiReadWritePolicy`. The adapter's archive-write nodes write through the
    repository and are not gated by this check.
  - **Reading the origin**: the GraphQL stream-data queries (`streamData.transientStreamDataQuery` /
    persisted queries: simple, groupingAggregation, downsampling, fieldFilter) accept `row_origin` (`manual` |
    `automated`; rows written before the origin column existed read `automated`), `manual_written_by`, `manual_written_at`,
    `manual_reason` as column / filter / groupBy paths on raw and time-range archives — the engine resolver
    (`StreamDataFieldResolver.CreateForArchive`) provides them, no asset-repo code. Rollup archives refuse them. There
    is no REST read endpoint for stream data. Test: `StreamData/StreamDataRowOriginQueryTests`.
- **TenantManager** - Manages stream data tenant contexts
- **StreamDataDatabaseManager** - Database operations for time-series data
- **StreamDataTenantContext** - Per-tenant stream data context
