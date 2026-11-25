# Changelog

## Unreleased

- AB#9788 Fix: a GraphQL mutation whose MongoDB transaction failed with `TransientTransactionError` (e.g. parallel
  creations of an auto-numbered type colliding on the counter, AB#9753) is executed again as a whole - new transaction,
  every resolver again from the variables - up to `GraphQl:TransientTransactionRetry:MaxAttempts` times (default 10) with
  a jittered backoff (20 ms doubling, at most 1 s), and only within a time budget
  (`GraphQl:TransientTransactionRetry:MaxElapsed`, default 10 s), so a long transaction the server aborted is not run
  again. Only mutations whose top-level fields are all `runtime` and that
  dispatch no CK method call are retried; `UnknownTransactionCommitResult` is never re-executed; errors without the label
  surface as before. Side effects and limits: `docs/graphql-transient-transaction-retry.md`. No CK model change.

- AB#8908 New: the unbounded-query guard (AB#8898) also checks the navigation connections inside an entity - typed
  navigation fields (`children`, …), the generic `associations(…)` of a typed entity and `associations { targets definitions }`
  of a generic entity. A to-many navigation that selects items without `first`, with `first` above `MaxPageSize`, or with
  more keys than `MaxPageSize` is logged and counted (`Warn`, once per request and field path, however many parents resolve
  it) or refused with `ASSET1007` (`Enforce`); the message and `parameters.fieldPath` name the field path. To-one
  navigations (multiplicity `One` / `ZeroOrOne`, e.g. `parent`) are exempt in every mode; whatever is not provably to-one
  (`includeIndirect: true`, unknown role, `direction: ANY`) counts as to-many. `rtId: null` / `rtIds: []` on a navigation
  are no key lookup (the navigation resolver would load every target). A negative `first` counts with its absolute value,
  which the connection resolvers page with (also for the top-level entry points: `first: -5000` was classified as within
  the limit). `Off` keeps today's behaviour. No CK model and no
  configuration change; docs `docs/runtime-query-guard.md`.
- AB#9608 Chore: NuGet dependencies updated within the .NET 10 line (FluentAssertions 8.11.0, Microsoft.NET.Test.Sdk 18.12.0, NLog.Web.AspNetCore 6.3.0, Testcontainers.MongoDb 4.17.0, coverlet.collector 10.1.1, xunit.runner.visualstudio 4.0.1, xunit.v3.mtp-off 4.0.3); the SeaweedFS test container maps its S3 identity file with the non-deprecated `WithResourceMapping` overload. No runtime behaviour change.
- AB#9325 Test: the System.Identity major-upgrade flow test sets up its tenant on System.Identity 2.30.0 through the
  service path (`ImportCkModelAsync(CkModelId)` from a 2.30.0 snapshot in the embedded catalog of the test host)
  instead of the caller import, which the engine will refuse for every `System`/`System.*` model
  (`CK_SERVICE_MANAGED_MODEL`). Test code only; it passes with the current and the refusing engine.
- AB#9578 New: the `BlueprintApplied` and `BlueprintUpdated` events on the distribution event hub carry the CK model
  dependencies of the blueprint (`CkModelDependencies`, as the blueprint engine reports them), so the Communication
  Controller and the AI service enable the capabilities a blueprint needs for its tenant without `EnableCommunication` /
  `EnableAi`. Additive: older consumers ignore the property. Needs the engine and octo-common-services packages of the same
  change.
- AB#8501 Fix: `POST {tenantId}/v1/Models/ImportCk` compiles a zip of the source `ConstructionKit/` folder (a zip that holds
  `ckModel.yaml`) instead of reading one of its files as a compiled model. The draft is compiled on the path of
  `Models/ValidateCk` (same extraction limits, same engine compiler, dependencies from the configured catalogs); the compiled
  model is what the service-managed refusal and the upgrade guard judge and what the import job imports, so a draft that
  validated is imported as validated. A draft with compile errors is refused with `400` problem details, `code`
  `CK_MODEL_COMPILE_FAILED`, and the findings in `errors` / `warnings` as `ValidateCk` reports them (file relative to
  `ConstructionKit/`, element path, no server path); nothing is queued. Compiled uploads (json, yaml, a zip of one) are
  unchanged. `ValidateCk` now also probes a zip by its header (not only by content type or `.zip` name) for `ckModel.yaml`.
- AB#8897 New: runtime-entity queries have a server-side time limit. Every query and count by type, by id, by type with
  navigation and by CK interface is sent to MongoDB with `maxTimeMS` from `RuntimeQuery:MaxTimeMs`
  (`OCTO_RUNTIMEQUERY__MAXTIMEMS`, default 120000 ms, 0 = off; helm value `assetRepository.runtimeQuery.maxTimeMs` of the
  `octo-mesh` chart). The limit applies to each MongoDB operation (page and count of a paged query each get
  the full limit). A query above the limit is stopped on the database and answered with the coded error `ASSET1008`
  (GraphQL: `extensions.code`, `errorId`, `parameters.ckTypeId` / `maxTimeMs` / `elapsedMs`; REST: `504` problem
  details with the same code) instead of the generic `ASSET1002` or a hung request, and logged once as a warning with
  tenant, CK type, duration, limit and error id. A query that completes after more than 80 % of its limit is logged as a
  warning; the client gets its result. Blueprint install, update and uninstall run without the limit (explicit
  override at the call site); the read-only update previews keep it. CK migrations and the blueprint of a new child
  tenant run without it in the engine. Not covered yet: association and deep-graph queries. Needs
  `Meshmakers.Octo.Runtime.Engine.MongoDb` with the time limit.
- AB#9057 New: the six REST column and computed-column endpoints are facades of the Archive column methods of AB#8600
  (System.StreamData 1.28.0): `POST {tenantId}/v1/streamdata/archives/{id}/computed-columns` → `Archive.AddComputedColumn-1`,
  `POST archives/{id}/columns` → `Archive.AddColumn-1`, `DELETE archives/{id}/computed-columns/{name}` →
  `Archive.RemoveComputedColumn-1`, `PUT archives/{id}/computed-columns/{name}` → `Archive.UpdateComputedColumnFormula-1`,
  `POST .../computed-columns/{name}/retry` → `Archive.RetryComputedColumnBackfill-1`, `POST .../computed-columns/{name}/repair`
  → `Archive.RepairComputedColumn-1`. Each call goes through the method gateway (caller tenant, role `StreamDataAdmin`, data
  permission on the archive) and the in-process dispatcher outside the request's Mongo session and writes exactly one Method
  audit event; it runs under the host's stopping token as before. Routes, parameters, status codes and bodies are unchanged
  (parity table in `docs/stream-data-management.md`); a Hidden attribute is still refused with `400` (AB#6247). While the
  method cannot serve the call (switch off, or System.StreamData older than 1.28.0) the endpoints keep their direct call behind
  `ArchiveMethodFallbackGuard` (AB#9337). **Changed answers:** a token of another tenant is `403` / `METHOD_FORBIDDEN`
  (`tenant-not-allowed`) and a caller whose data permission refuses the archive `403` / `METHOD_FORBIDDEN` (`data-permission`),
  on both paths - before, these endpoints had neither check; update, retry and repair of an unknown computed column answer
  `400` with the engine message on both paths (update answered an unhandled `500` before); a `resultType` number outside the
  enum on add is `400` problem details `METHOD_VALIDATION_FAILED` instead of an engine error; on the method path a change that
  takes longer than the methods' 60 s limit answers `504` / `METHOD_TIMEOUT` while it keeps running (the direct call had no
  limit). The old WARN line with the archive
  id and the engine message of an engine refusal is gone (Debug with the exception type; the guard's WARN line on the
  fallback). REST is not deprecated and has no Sunset header.
- AB#9349 Fix (Security): on the archive fallback path (`ArchiveMethodFallbackGuard`) an engine refusal (`400`) of the REST
  lifecycle endpoints, `recompute` and `backfill-from-source` no longer logs a second WARN line with the archive or rollup id and
  the engine message; the endpoint logs it at Debug with the exception type only, the guard's one WARN line stays, and the caller
  still gets `400` with the engine message. **Changed answer:** in a tenant with a strict audit (`FailCallOnAuditFailure`) whose
  audit write fails, an engine refusal on the fallback now answers `METHOD_OUTCOME_UNKNOWN` (REST `500`, the mutation's error)
  instead of `400` / `STREAMDATA_*`, the answer of the method path's audit filter; refusals of the guard itself stay the refusal.
  Authorised callers otherwise keep their status codes and bodies.
- AB#9337 Fix (Security): archive calls that do not run through their CK method get the checks of the method path. Affected: the
  REST endpoints `POST {tenantId}/v1/streamdata/archives/{id}/activate`, `disable`, `enable`, `retry`, `DELETE
  archives/{id}`, `freeze`, `unfreeze`, `rewind`, `recompute` and `backfill-from-source` and the deprecated mutations
  `activateArchive`, `disableArchive`, `enableArchive`, `retryArchiveActivation`, `deleteArchive`, `freezeRollupArchive`,
  `unfreezeRollupArchive` and `rewindRollupWatermark` while the method cannot serve them (the **fallback**:
  `Methods:StreamData:Enabled=false`, or a tenant whose System.StreamData does not declare the method), and the deprecated mutations
  `recomputeArchive` and `backfillRollupFromSource` always (they never went through a method). Before, these calls ran the lifecycle
  service directly without the caller-tenant check, the data-permission check and the audit. Now `ArchiveMethodFallbackGuard`
  runs the caller-tenant check of the gateway (`CkMethodCallerTenantCheck`, including `TenantAuthorization:CrossTenantServiceClientIds`)
  and the data-permission check on the archive with the same rule and archive resolution as the method path, writes exactly one
  Method audit event per call (transport `fallback`, also for a refusal) and one WARN line (operation, method id, reason
  `switch-off` / `method-not-declared` / `not-routed`; never target id, parameters, user or token). **New refusals:** a token of
  another tenant (`403` / `METHOD_FORBIDDEN`, reason `tenant-not-allowed`, also while the HTTP tenant gate runs in LogOnly) and a
  caller whose data permission refuses the archive (`403` / `METHOD_FORBIDDEN`, reason `data-permission`; an archive the caller
  cannot see answers like a missing one), on REST and on the mutations, including `recomputeArchive` and
  `backfillRollupFromSource`. Authorised callers keep their answers (`204`, `400`, the job); the method path is unchanged. No
  option turns the checks off. The data-permission answer and the audit event of the gateway moved into the shared
  `CkMethodCallChecks`, which the gateway and the guard both use.
- AB#9056 New: `System.StreamData/RollupArchive.Recompute-2` and `BackfillFromSource-2` (System.StreamData 1.30.0, GraphQL
  fields `recompute2` / `backfillFromSource2`), the asynchronous recompute and backfill: the call returns an operation handle at
  once and `runtime.operations` shows the operation `RUNNING`, then `SUCCEEDED` with the job (`RecomputeJobInfo`) or `FAILED`
  with `STREAMDATA_RECOMPUTE_FAILED` (`jobRtId`, `reason`) for a job that ended Failed. `BackfillFromSource-2` reports the queued
  job (`QUEUED`, `jobRtId`) and follows it until it ended. The work runs detached from the caller in this service's operation
  runner (`AddCkMethodOperations`); roles as before (`StreamDataAdmin`), no right widened. `Recompute-1` and
  `BackfillFromSource-1` keep working and are deprecated; the mutations `recomputeArchive` and `backfillRollupFromSource` name the
  `-2` versions. The REST endpoints `POST {tenantId}/v1/streamdata/archives/{id}/recompute` and `/backfill-from-source` are
  facades of the `-2` methods: route, parameters and answer unchanged (`RecomputeJobInfo`, a Failed job with `200`, `204` for an
  empty source); `recompute` answers when the operation ended, `backfill-from-source` once the operation reported its queued job
  (bounded by `Methods:StreamData:BackfillQueueTimeout`, default 60 s, then `504` naming the operation). **Behaviour change on an
  error path:** recompute/backfill-from-source on a deleted or non-rollup archive now answers 400 (not found) instead of 200 with
  a Failed job (the gateway refuses the call before any job exists; no job is persisted, no operation accepted). The polling of
  the job and of the operation record retries transient read errors (`Methods:StreamData:OperationPollInterval` and
  `BackfillQueueTimeout` must be greater than zero, checked at startup). Other behaviour changes as for the lifecycle facades: one Method audit event per call (outcome `Accepted`), a data permission that refuses the rollup refuses the
  call (`403`), a token of another tenant is `403` `METHOD_FORBIDDEN` / `tenant-not-allowed`. No Sunset header. With
  `Methods:StreamData:Enabled=false`, or in a tenant whose System.StreamData is older than 1.30.0, the endpoints keep their direct
  call. Needs octo-construction-kit-engine with System.StreamData 1.30.0 (minEngineVersion 3.5.67).
- AB#9293 Fix (Security): `POST {tenantId}/v1/Models/ImportCk`, `ImportFromCatalog` and `ImportFromCatalogBatch` (and with them
  octo-cli, SDK and MCP) refuse every service-managed CK model - `System` and `System.*`, name compared case-insensitively -
  whatever its version (upgrade, same version, downgrade) and whoever calls, `forceBreakingUpdate` included: `403` problem
  details with `code: CK_SERVICE_MANAGED_MODEL`, `message` and `models`, logged at WARN; nothing is cached, queued or imported.
  The check reads the model the uploaded or catalog bytes actually parse to (every CK model entry of a zip, the requested and the
  loaded ids of a catalog import), and `ImportCk` caches exactly the bytes it judged. **Behaviour change:** before, a caller with
  `DataModelManagement` could replace a service-managed model with a self-built one (for example System.Identity without the
  write-channel restriction of AB#9291) or downgrade it explicitly; that was a bug. Service-managed models reach a tenant only
  through the services' embedded import (recovery: `ClearCache` / `ReRunTenantSetup`), blueprint and system import and tenant
  restore. Also fixed: a zip upload to `ImportCk` / `ValidateCk` was answered with 500 by the real serializer (the zip entry
  stream cannot be rewound); entries are buffered now. A file or zip entry that cannot be read as a compiled model is a `400`
  (was a `500` with the real serializer), and a zip model entry that cannot be read refuses the upload instead of being skipped.
- Refactor: the REST archive lifecycle and rollup endpoints (`POST {tenantId}/v1/streamdata/archives/{id}/activate`,
  `/disable`, `/enable`, `/retry`, `DELETE .../archives/{id}`, `POST .../archives/{id}/freeze`, `/unfreeze`, `/rewind`) and the
  deprecated mutations `activateArchive`, `disableArchive`, `enableArchive`, `retryArchiveActivation`, `deleteArchive`,
  `freezeRollupArchive`, `unfreezeRollupArchive` and `rewindRollupWatermark` call the Archive methods of System.StreamData
  (`Archive.Activate-1` … `RollupArchive.RewindWatermark-1`) through the method gateway and the in-process dispatcher instead
  of the lifecycle services: one Method audit event per call, the handler outside the request transaction. Routes, bodies,
  status codes and the mutation schema are unchanged; REST gets no Sunset header. Behaviour changes: a data permission that
  refuses the archive now refuses the call (REST `403` / `METHOD_FORBIDDEN`, before there was no data-permission check); a
  caller token of another tenant is refused by the method with `METHOD_FORBIDDEN` / `tenant-not-allowed` (REST `403` problem
  details) also while the HTTP tenant gate only logs; platform failures of the method answer with their documented codes
  (`METHOD_VALIDATION_FAILED` `400`, `METHOD_TIMEOUT` `504`, ...). With `Methods:StreamData:Enabled=false` (default true)
  the endpoints and mutations keep their direct lifecycle call, and so they do in a tenant whose CK model does not declare
  the method (System.StreamData older than the methods, or not resolved), instead of answering `403`.
- New: `System.StreamData/Archive.RedactHiddenColumns-2` (System.StreamData 1.29.0, GraphQL field `redactHiddenColumns2`), the
  redaction of Hidden archive columns with the columns the administrator confirmed: required `expectedColumns`; when the Hidden
  columns at execution time are not exactly that set, the call answers `STREAMDATA_REDACTION_COLUMNS_CHANGED` (parameters
  `expectedColumns`, `currentColumns`) and drops nothing. Same role (`StreamDataAdmin`), confirmation and result as `-1`, which
  is deprecated (it drops whatever is Hidden when it runs). Both handlers share one implementation. Needs System.StreamData 1.29.0
  (octo-construction-kit-engine).
- AB#8600 New: archive columns and computed columns as CK methods of System.StreamData 1.28.0, handled in-process like the
  other archive methods: `Archive.AddComputedColumn-1`, `AddColumn-1`, `RemoveComputedColumn-1`, `UpdateComputedColumnFormula-1`
  (answer `ArchiveTransitionResult`), `RetryComputedColumnBackfill-1` and `RepairComputedColumn-1` (answer the queued job as
  `RecomputeJobInfo` with `computedColumnName`). Role `StreamDataAdmin`, the mutations' error codes; a path that reaches a Hidden
  attribute is refused as with `addArchiveColumn`. Differences: `STREAMDATA_NOT_ENABLED` without stream data, and
  `STREAMDATA_ERROR` (engine message) for an unknown computed column in update, retry and repair, where the mutations answer
  their generic error (removing an unknown column changes nothing, as with the mutation). The mutations `addComputedColumn`, `addArchiveColumn`, `removeComputedColumn`,
  `updateComputedColumnFormula`, `retryComputedColumnBackfill` and `repairComputedColumn` keep working and are `@deprecated` with
  the method id, so every `streamData` mutation is deprecated now; a unit test fails for a new one without a method. The rollup
  methods' `RecomputeJobInfo` also carries `computedColumnName` (null for a rollup job). Needs System.StreamData 1.28.0
  (octo-construction-kit-engine with AB#8600).
- AB#6247 New: `System.StreamData/Archive.RedactHiddenColumns-1` (System.StreamData 1.27.0), the documented way out of an
  archive the engine set to `Failed` because a CK model change made one of its columns Hidden. **Destructive:** drops these
  columns and their stored values from the table and the definition, audited, and activates the archive again; requires
  `StreamDataAdmin` and `confirmDataLoss: true` (`STREAMDATA_CONFIRMATION_REQUIRED`), refused while a rollup aggregates a
  column (`STREAMDATA_REDACTION_BLOCKED`). Such a Failed archive now keeps answering stream-data queries for its history
  without the Hidden columns, and `Archive.StatusReason` / `Archive.HiddenColumns` say why. A CK model import in another
  service (identity, communication controller, platform services) now triggers the same re-check here through the
  tenant-update event. Needs octo-construction-kit-engine, octo-construction-kit-engine-mongodb and octo-common-services
  with AB#6247.
- AB#8998 Fix: the GraphQL schema build survives a CK cache unload during the build. When a `PreUpdateTenant` (CK import,
  revalidation) or the stale-type fallback unloaded the tenant's CK cache between the load and `Initialize`, the request
  failed with HTTP 500 "Schema creation failed" (inner "CkCache for tenant 'x' not found"; prod-2, tenant sbeg,
  2026-11-15). `SchemaContext` now builds once more from a fresh load when a `CkCacheException` meets a CK cache that was
  unloaded or reloaded during the build (WARN with AB#8998); one retry, no loop. A CK error on a cache that stayed
  loaded is not retried. AB#8667 covered only a reload. The schema build log line gains `attempt {Attempt}`.

- AB#8896 New: a paged runtime-entity query counts the matching entities only when the selection needs the total
  (Feature AB#8793). `runtime.<CkType>`, `runtime.runtimeEntities`, persisted query `rows` and transient query `rows`
  (standard queries) pass `includeTotalCount` to the engine: true when `totalCount` or `pageInfo` is selected or the
  query pages from the end (`last`/`before`); otherwise no count query runs and only the page is read. With the
  engine-mongodb change of the same story a paged query no longer loads every matching document (no `$facet`).
  Queries by `rtId`/`rtIds` and aggregation queries are unchanged. The engine now counts in a read of its own after
  the page, so entities deleted in between can report a total below the page end; the connection then reports the
  page end as the total (`ConnectionUtils.TotalCoveringThePage`) instead of dropping edges or failing the slice check.
  Needs octo-construction-kit-engine-mongodb with AB#8896.
- AB#8986 New (security): an administrator of a parent tenant calls `System.Bot/TenantRepository.Dump-1`, `Restore-1`,
  `ExportArchiveData-1` and `ImportArchiveData-1` on a registered child tenant with the parent's user token and reads those
  operations through `runtime.operations`, mirroring the REST jobs rule (octo-common-services `CkParentTenantAdministrationMethods`).
  The GraphQL endpoint carries the per-request predicate `GraphQlParentTenantAdministrationRequest` (only a single
  `application/json` POST made exclusively of those calls or operation reads). **Stricter:** `runtime.operations` no longer
  answers a user token of another tenant that is not a parent administrator of the route tenant (before, on hosts where the HTTP
  gate only logs, such a token read its own records there). Needs the octo-common-services package with AB#8986.
- AB#9099 Fix: the stream-data recompute integration tests follow the late-data rules of AB#9016 (engine: scoped, debounced
  recompute; engine-mongodb: in-place refresh of the affected series). Six tests of `RollupMultiSourceRecomputeTests` and
  `RollupLateDeliveryLastBucketTests` called the drain right after a late write, while the default
  `StreamData:Recompute:LateDataDebounce` of 5 min held the window back, so no recompute ran yet (main CI 70436). No recompute was
  lost: with the debounce at zero the late values land in the right buckets, the last hourly bucket before a pause included.
  `ImmediateSweepStreamDataFixture` now sets the debounce to zero, and the tests that check a late delivery also assert that the run
  refreshed only its series. The new `DefaultDebounceStreamDataFixture` keeps the production debounce:
  `RollupLateDeliveryDebounceTests` checks that the first drain holds a late delivery back, and `RollupManualWriteTests` moved
  there, so a manual write is still shown to run on the first drain. Tests only; production configuration and behaviour unchanged.

- AB#8966 New: blueprint uninstall reports what it erased and what it kept, and has a dry run. The engine now erases unedited
  tenant-owned seed entities on uninstall and keeps edited and referenced ones (octo-construction-kit-engine, same story). The
  uninstall result (`DELETE {tenant}/v1/blueprints/{name}`, GraphQL `blueprints { uninstall }`) gains `dryRun`,
  `deletedEntities` (key, ckTypeId, entityId, wellKnownName, tenantOwned) and `tenantOwnedKept` (the same identity plus `reason`
  `Edited` | `Referenced` | `NotComparable` | `EraseFailed` and `referencedBy`); identity only, never attribute values. New dry run:
  `POST {tenant}/v1/blueprints/{name}/uninstall/preview?cascade=` (same status codes as the uninstall, `DataModelManagementPolicy`)
  and the GraphQL argument `uninstall(dryRun: true)`. The dry run has a route of its own instead of a `DELETE` flag, so an
  asset repository without AB#8966 answers an empty 404 instead of ignoring the flag and uninstalling; "not installed" is a 404
  with an `OperationFailedErrorDto` message on both routes (was an empty 404 on `DELETE`). Additive; older clients keep working.

- AB#8722 Fix: the asset repository starts reliably on the first rollout of the System.StreamData archive methods (AB#8598), also with
  several replicas starting at once. On a system tenant without System.StreamData the import at startup unloaded the system tenant's CK
  cache (own import, or a cache that lacked the model another replica had just installed) and nothing loaded it again before the
  startup validation of the in-process method registrations, which reads only a loaded cache: the pod stopped with "the CK cache of
  tenant(s) 'octosystem' has no such method" and started after a restart (test-2, 2026-11-10). `SystemTenantStreamDataModelImport` now
  loads the cache after the import and checks that it holds the model; a cache loaded from the state before the import is loaded again.
  A registration for a method the model does not have still fails the start. Startup behaviour only; no CK model change.

- AB#8898 New: unbounded runtime-entity query guard at the GraphQL API boundary (Feature AB#8793). A query on
  `runtime.<CkType>`, `runtime.runtimeEntities`, persisted query `rows` or transient query `rows` that selects
  `items`/`edges`/`nodes` is unbounded when it has no `first` or a `first` above `RuntimeQueryGuard:MaxPageSize` (default 1000),
  unless it is a key lookup (`rtId`/`rtIds`) with at most that many keys; a `first` within the limit bounds the query whatever
  the key count. `RuntimeQueryGuard:Mode` (`OCTO_RUNTIMEQUERYGUARD__MODE`): `Off` (no
  check), `Warn` (default: the query runs, one warning log line per query and the counter `octo.asset.runtime_query.unbounded`,
  Prometheus `octo_asset_runtime_query_unbounded_total`, labels `tenant`, `ck_type`, `client_id`, `mode`) or `Enforce` (refused
  with `ASSET1007` before the entities or rows are read, message naming CK type and limit, `extensions.errorId` and `extensions.parameters`;
  never capped). totalCount-only selections and aggregation queries are always bounded; in-process engine
  calls (`ITenantRepository`) are never checked or counted. The asset repository has no REST runtime-entity query endpoint (REST
  exports are background jobs), so REST needs no guard. ASSET1007 moves to the coded error contract of AB#6244 when it exists.
  See `docs/runtime-query-guard.md`.
- AB#8895 New: a runtime-entity query that selects only `totalCount` (no `items`, `edges`, `pageInfo`, `aggregation` or
  `fieldAggregations`; aliases and `__typename` allowed) is counted on the server without loading any document - the typed
  query (`runtime.<type>`), the generic `runtime.runtimeEntities(ckId: ...)` and the `rows` of a simple persistent query.
  The count is the same as with items selected (data permissions, filters, search and the archived flag apply; paging
  arguments do not change a total count). Before, such a query without `first` loaded every matching document into the
  asset repository (ASSET1002 OUT_OF_MEMORY on large entities, AB#8793). Needs octo-construction-kit-engine-mongodb with
  AB#8895 (`ITenantRepository.CountRtEntitiesByTypeAsync`, `CountRtEntitiesGraphByTypeAsync`).
- AB#8778 New: blueprint update preview and result carry two more tenant-owned lists, `TenantOwnedRefreshed` (unedited
  tenant-owned seed entities that receive the changed seed; not in `Safe` mode) and `TenantOwnedUpdateAvailable` (entities the
  update keeps although the seed changed, because their content differs from the installed seed or the comparison was not
  possible; subset of `TenantOwnedSkipped`) - REST `BlueprintUpdatePreviewDto` / `BlueprintUpdateResultDto`,
  GraphQL `previewUpdate` / `applyUpdate` (`tenantOwnedRefreshed`, `tenantOwnedUpdateAvailable`). Additive, empty by default.
  Needs octo-construction-kit-engine with AB#8778.
- AB#8599 New: rollup control and recomputation as CK methods of System.StreamData 1.25.0, handled in-process like the AB#8598
  archive methods: `RollupArchive.Freeze-1`, `Unfreeze-1`, `RewindWatermark-1` (answer `ArchiveTransitionResult`),
  `Recompute-1` and `BackfillFromSource-1` (answer `RecomputeJobInfo`, synchronous like the mutations; `BackfillFromSource-1`
  answers no job - `value` is null - when the source holds no data to backfill, so there is nothing to poll). Role `StreamDataAdmin`,
  the mutation's error codes. The mutations `freezeRollupArchive`, `unfreezeRollupArchive`, `rewindRollupWatermark`,
  `recomputeArchive` and `backfillRollupFromSource` keep working and are `@deprecated` with the method id. Needs System.StreamData
  1.25.0 (octo-construction-kit-engine with AB#8599).
- AB#8707 Fix: the archive create methods (`TimeRangeArchive.CreateArchive-1`, `RollupArchive.CreateArchive-1`) bind the generated
  parameter records of System.StreamData (`TimeRangeArchiveCreateArchiveParameters`, `RollupArchiveCreateArchiveParameters`); the
  plain look-alike classes of AB#8598 are removed. Behaviour unchanged (same validation, same source rules, same answers). Needs
  octo-common-services with AB#8707 (`CkMethodJson` binds record arrays of generated records).
- AB#8776 Fix (security): the CK method gateway refuses a caller token of another tenant first, before the role check, the target
  load and the parameter validation: `METHOD_FORBIDDEN` with `reason = tenant-not-allowed` for an existing and a missing target, a
  missing role and invalid parameters alike (before: `METHOD_TARGET_NOT_FOUND` or a role refusal told the caller whether an entity
  exists in a tenant it has no access to). Same rule as the in-process filter of AB#8711 (allow-listed service clients pass),
  independent of the HTTP tenant gate's mode. Needs octo-common-services with AB#8776 (`CkMethodCallerTenantCheck`). Test
  principals of the method tests now carry `tenant_id`, as every identity token does.
- AB#8711 Refactor: the archive CK methods (AB#8598) no longer check the caller tenant themselves; `ArchiveMethodCallerTenantGuard`
  is removed. The check moved into the in-process transport of octo-common-services (`CkMethodCallerTenantFilter`), which applies
  the same rule to every in-process method of this service before any handler runs: a caller token issued for another tenant is
  `METHOD_FORBIDDEN` with `reason = tenant-not-allowed`, also while the HTTP tenant gate only logs; clients on
  `TenantAuthorization:CrossTenantServiceClientIds` keep working. Answer unchanged for the archive methods (code and reason; the
  error now also names `methodId`). Needs octo-common-services with AB#8711.
- AB#8709 Fix (security, **breaking for callers without the role**): the stream-data REST endpoints that change an archive or
  its columns require the tenant role `StreamDataAdmin` on top of the `octo_api` scope (new `StreamDataAdministrationPolicy`),
  the same rule as the `streamData` GraphQL mutations and the Archive CK methods. Covered: archive lifecycle
  (`activate`, `disable`, `enable`, `retry`, `DELETE archives/{id}`), rollup `freeze` / `unfreeze` / `rewind` / `recompute` /
  `backfill-from-source`, `POST rollups/repair` (also the dry run), computed columns (add, formula change, remove, retry, repair)
  and `POST archives/{id}/columns`. Before, they carried the scope-only `TenantAssetApiReadWritePolicy`, so any `octo_api` token
  without a stream-data role could activate, disable and delete archives (reproduced on test-2). **Callers without
  `StreamDataAdmin` now get `403`** with `{"message": "...", "code": "StreamDataAdminRequired"}` and nothing changes; grant the
  role to users and client-credentials clients that run these calls (the group `TenantOwners` carries it). Unchanged: tenant
  `enable` / `disable` (`TenantManagement`), `insert` / `insertTimeRange` (scope for automated points, `StreamDataAdmin` for
  manual points, AB#8590) and the reads.
- AB#8705 New: the asset repository tells the CK engine whether a write comes from a person or a service client.
  `GraphQL.Helpers.GetSecurityContext` sets `RtSecurityContext.PrincipalKind` from the token (`Security/CallerPrincipalKind`):
  user tokens (authorization code, device code, cross-tenant token exchange) are `Interactive`; client-credentials tokens
  (no `sub`), delegated/on-behalf-of and impersonated tokens (`act` or `amr` `delegation`/`impersonation`) are `Client`
  (decision Odin 2026-11-10); a token with neither `sub` nor `client_id` is `Unknown`. It covers GraphQL, REST and the
  ImportRt user path (`ImportRtCommandRequest.InitiatedByPrincipalKind`, consumed by bot services). CK types with a
  `writeAccess` block (engine AB#8574) refuse `Client`/`Unknown` writers as declared (message 144, nothing persisted);
  unrestricted types are unaffected. No identity change. Docs: `docs/principal-kind.md`. Needs the engine with
  `RtPrincipalKind` (octo-construction-kit-engine #362), engine-mongodb #240 (persists the block) and common-services
  `ImportRtCommandRequest.InitiatedByPrincipalKind`.

- AB#8679 Fix: when the archive write stays incomplete (CrateDB did not store every row, also after one retry), `insert` and
  `insertTimeRange` answer 503 with a short `application/problem+json` body (`archiveRtId`, `rowsInAffectedWrite`,
  `rowsNotStored`) and `Retry-After: 30`. The counts belong to the one write statement that failed, not to the request;
  other points of the request may have been stored, so the caller reloads before retrying. The body no longer carries the exception text with the internal `schema.table` and the CrateDB breaker
  detail; the full detail is logged server-side.
- AB#8590 Fix: `insert` and `insertTimeRange` answer 400 instead of 500 for an `archiveRtId` that is not a runtime id (naming
  the field), for a point `rtId` of `insertTimeRange` that is not one (naming the point and the field, like `insert`), and for a
  `null` array element (`Point i is null.`); nothing is read or written.

- AB#8667 Fix: the GraphQL schema of a tenant follows its CK cache. The cached schema remembers the CK cache load it was
  built from and is rebuilt when the cache was unloaded or reloaded since - also when no `PosUpdateTenant` reached this
  process (a reload by the AB#5415 fallback or the display-rule sweeper, or an event published before the service listened
  to the bus at start). Before, a schema built at start from a cache that predated another service's import (e.g. the
  System.StreamData bump that adds the time-range archive types) stayed without the new types until a manual ClearCache,
  and Studio showed "Failed to load archive content". An unchanged cache keeps the cached schema. Needs engine-mongodb
  AB#8667 for the cache itself to drop the stale model at start.

- AB#8622 Fix: a manual point of `insert` and `insertTimeRange` may leave the ranking columns out (the column keys of the
  archive's `ConflictPrecedence`); they keep their stored value. Every other ingested column still has to be named (AB#8590).
  Before, a correction of a row whose ranking column is stored as NULL was impossible: naming the column needs a value, and
  `null` is refused. The null-refusal message no longer tells a manual point to send a value only.
- AB#8622 Fix: `insert` and `insertTimeRange` refuse an explicit `null` on a ranking column (a column key of the archive's
  `ConflictPrecedence`; `$Origin` is no column) with 400 naming the point and the key; nothing of the batch is written. Applies to
  automated and manual points (`ArchivePointValidation.CheckColumns`, keys matched like the storage column). The engine refuses it
  as well (engine-mongodb AB#8622). A ranking column the point leaves out keeps its stored value, and the conflict guard compares
  the point against that merged value. **Behaviour change:** a REST caller that sent `DataQuality: null` or
  `SourceDocumentDate: null` to a ranked archive now gets 400; a manual point leaves such a column out instead (see the
  ranking-column exemption above).
- AB#8622 Docs: `insert` and `insertTimeRange` name the absent-versus-null rule of the archive writes (engine-mongodb AB#8622):
  an attribute the point does not name keeps the stored value of its column, an attribute sent as `null` clears it, and a
  new row stores NULL for every column the point does not name. **Behaviour change for automated points** (no code change
  here, it comes with the engine package): a partial re-delivery no longer clears the columns it did not send. The REST
  body keeps the two apart (`RtAttributesConverter` keeps a JSON `null` as a key with a null value). Manual points must
  still name every ingested column (AB#8590): a manual point protects the whole row.
- AB#8590 Fix: `insertTimeRange` refuses unknown and computed columns and sub-millisecond windows, with the same rules as the raw
  `insert` (shared `ArchivePointValidation`). A key that is no ingested column of the archive, a computed column, or a second key for
  the same column used to be dropped by the repository while the call answered 200, so a manual correction looked stored. Such a
  point, and a `from` or `to` that is missing or has sub-millisecond precision, now answers 400 naming the point and the field, and
  nothing of the batch is written. Applies to automated and manual points; REST callers that send extra keys must drop them
  (pipeline nodes write through the repository and are not affected). `from`/`to` without offset are read as UTC, with offset converted.
- AB#8590 Fix: `insertTimeRange` refuses a manual point that leaves columns out. The time-range upsert writes every ingested
  column of the archive and binds an unsent attribute as NULL, so a manual correction that sent only the changed column
  silently cleared the stored values of the other columns. A point with origin `manual` must now name every ingested column
  (explicit `null` allowed), exactly like the raw `insert`; otherwise the batch answers 400 listing the missing columns and
  nothing is written. Same check as the raw endpoint (`ArchivePointValidation`, shared). Automated points are unchanged.
  Callers that send manual points (Studio sends the whole row) must send every column.
- AB#8590 New: `POST {tenantId}/v1/streamdata/archives/{archiveRtId}/insert` writes and corrects rows of a raw archive with
  `origin` (`manual` | `automated`) and an optional `reason`, through the existing raw `IStreamDataRepository.InsertAsync` (origin,
  writer, manual protection and write result since AB#5968). Row identity: archive + `rtId` + `timestamp` (UTC, milliseconds);
  protection is per row. A manual point must name every ingested column (explicit null allowed). Client errors (invalid id,
  origin, reason without manual, token without writer, ckTypeId mismatch, unknown or computed column, sub-millisecond timestamp,
  time-range or rollup archive, value not fitting its column) answer 400 naming point and field; 200 with the write result, 204
  for an empty batch, 409 when a manual row was not stored. The ckTypeId check of `insertTimeRange` is shared, unchanged.
- AB#8590 New (security): a write with origin `manual` - on the new `insert` and on the existing `insertTimeRange` - needs the
  tenant role `StreamDataAdmin` (the role Studio checks for corrections) besides the write scope; without it the whole batch
  answers 403 `ForbiddenErrorDto` naming the role and nothing is written, also when it mixes manual and automated points.
  Automated writes keep the scope-only check (`TenantAssetApiReadWritePolicy`), so pipelines and adapters are unaffected.
  **Before the rollout**, grant `StreamDataAdmin` to every user or client that sends manual points over REST (pipeline nodes
  write through the repository and are not affected).

- AB#8598 New: the archive lifecycle as CK methods of System.StreamData 1.24.0, handled in-process (`StreamData/Methods/`):
  `Archive.Activate-1`, `Disable-1`, `Enable-1`, `RetryActivation-1`, `DeleteArchive-1`, `TimeRangeArchive.CreateArchive-1`,
  `RollupArchive.CreateArchive-1`. Same engine calls, result shapes and STREAMDATA_* error codes as the mutations; role `StreamDataAdmin`
  (METHOD_FORBIDDEN without it). The seven `streamData` mutations they replace keep working and are deprecated with
  `Use <method id>`. Switch `Methods:StreamData:Enabled` (default true). At startup the embedded System.StreamData is imported into the
  system tenant (Order 12) so the registration validation finds the methods. Needs the engine package with System.StreamData 1.24.0.
  A caller token of another tenant is refused with `METHOD_FORBIDDEN` / `tenant-not-allowed` (claim rule of the bus host, also
  while the HTTP tenant gate runs in LogOnly).

- AB#8647 New: the transient `downsampling` query takes the optional argument `groupByColumnPaths: [String!]` (supported:
  `["rtId"]`) and returns one row per series and bin instead of one total per bin. The value goes to the engine's
  `StreamDataDownsamplingQueryOptions.GroupByColumnPaths` (AB#7360); the series key is the cell `rtId` after `timestamp` and the
  first entry of `columns`. With local-day bins (`timeZone` / `binOrigin` / `binInterval`) every series gets every bin of the range,
  an empty one with `COUNT` 0 and the other aggregations null, on raw archives and on `CalendarDay` rollups (a series without data in the
  whole range gets no rows; the engine builds the whole result and refuses more than 100,000 bins x series rows, independent of the
  page size); with fixed-width bins (`limit`) there is no per-series fill and no such cap, an empty bin stays one row with a null `rtId`. `totalCount` and paging count series x bins. Other paths are refused with an error that names them (the engine's downsampling row carries
  no other group value; use `groupingAggregation`). Additive: without the argument the query, its engine call and its cells are
  unchanged; the simple-downsampling path (AB#4233) keeps its cells. Code `GraphQL/StreamDataDownsamplingGroupColumns`. Tests
  `UnitTests/GraphQL/StreamDataDownsamplingGroupColumnsTests`, `IntegrationTests/StreamData/DownsamplingGroupByGraphQlTests`
  (3 series x gas days 23/24/25, empty gas day Count 0 on raw and rollup, paging, ungrouped unchanged, introspection).

- AB#8559 Fix (security, staged): stream-data reads require one of the tenant roles `StreamDataReader`, `StreamDataWriter`
  or `StreamDataAdmin`. Until now every authenticated token of a tenant read all stream data, because the GraphQL endpoint only
  requires an authenticated user and the data-policy check (AB#4973) is open for CK types without a policy; the seeded role
  `StreamDataReader` was never checked. The requirement covers every stream-data read resolver (transient `simple`,
  `aggregation`, `groupingAggregation`, `downsampling`, the persisted `streamDataQuery` and its `rows` / `aggregations`; all go
  through `DataPermissionStreamGuard.EnsureStreamReadAllowedAsync`, the role check runs before the data-policy check) and is
  staged by `StreamDataReadAuthorization:RoleEnforcement` (env `OCTO_STREAMDATAREADAUTHORIZATION__ROLEENFORCEMENT`):
  `Off` (unchanged), `Warn` (**default of this release**: the read goes ahead and a warning names tenant, query kind, subject
  and client id, once per caller, tenant and query kind within `StreamDataReadAuthorization:WarnLogInterval`, default 1 h) and
  `Enforce` (refused with the GraphQL error code `StreamDataReadRoleRequired`). In-process executions without a principal and
  system security contexts always read. **Before switching an environment to `Enforce`**, every caller of the warn log must
  hold a stream-data role. Only callers that read stream data **through the asset-repository API with their own token** are
  affected: app clients and service accounts that call the GraphQL API with a client-credentials token, and Grafana / Excel /
  Studio users that read time series. Pipelines the mesh adapter runs (e.g. EC allocation, billing, simulation) read archives
  in-process through the engine and never reach this gate (corrected by AB#8583). **Operator note:** pipeline service accounts
  need no manual grant - the communication controller adds `StreamDataReader` to declarative pipeline service accounts on
  reconcile (AB#8563), and identity grants it to legacy `octo-pipeline-sa-*` accounts at every tenant setup (AB#8581); the
  manual `octo-cli -c AddClientToRole ... StreamDataReader` step is no longer needed once those versions run and a
  reconcile or tenant setup has granted the role (until then the account may lack it and its reads fail under `Enforce`).
  Archive metadata queries (`rollupsFor`, `coverageFor`, `recomputeJobsFor`, `archivesStorageStats`, `rollupQueryMetadata`, `resolveSeriesQuery`) are not gated. Tests
  `UnitTests/GraphQL/StreamDataReadRoleGateTests`, `IntegrationTests/StreamData/StreamDataReadRoleGateTests` (mode x caller x
  query kind).
- AB#8529 New (**breaking change for external clients**): changing a tenant's data model requires the tenant role
  `DataModelManagement`. `DataModelManagementPolicy` is now scope `octo_api` AND the role (central `AddTenantRolePolicy`; until now
  it was scope-only, so every `octo_api` token passed it), and it guards `Models/ImportCk` (before: any read-write token),
  `Models/ImportFromCatalog`, `Models/ImportFromCatalogBatch` (octo-cli `FixAll`), `blueprints/apply`, `blueprints/updates/apply`,
  `DELETE blueprints/{name}` (before: any read-write token) and the CK model / blueprint catalog refresh endpoints. The GraphQL
  blueprint mutations `install`, `applyUpdate`, `uninstall` require `DataModelManagement` in addition to `AdminPanelManagement`.
  A caller without the role gets `403` with `{"message": "...", "code": "DataModelManagementRequired"}` (GraphQL: error code
  `DataModelManagementRequired`) and nothing changes. Members of `TenantOwners` (tenant creators, provisioned administrators,
  scratch-tenant creators) already hold the role; other users and client-credentials clients that import models or install
  blueprints must be granted it (`octo-cli -c AddUserToRole -un <user> -r DataModelManagement`, or a group that carries it).
  Service-internal installs (service CK models, service-managed blueprints) run in-process and are not affected. Needs
  octo-common-services with the AB#8529 error code. Tests `UnitTests/Configuration/DataModelManagementAuthorizationTests`,
  `UnitTests/GraphQL/BlueprintsMutationCkUpgradeBlockedTests`.
- AB#6926 New: the GraphQL CK meta reports the execution mode of a method. `CkMethodExecution.mode: String!` is the effective
  mode: `sync` (default when undeclared, also for tenant documents written before the field) or `async` (the call returns an
  operation handle once the handler accepted the work; `timeoutSeconds` bounds that accept step). Additive on the v2-only meta
  type; queries that do not select the field are unchanged. Test `IntegrationTests/GraphQL/CkV2/CkMetaIntrospectionTests`
  (async `IssueToken-1`, explicit sync `ResetPassword-2`, undeclared `Touch-1`, a query without the field).
- AB#8322 Fix: a query-row update of a record member keeps the other members of the record. `runtimeQuery(rtId) { update }`
  writes cells such as `detail.label`, `detail.inner.text` or `entries[0].label`; the update document held only the cell values,
  so the whole stored record was replaced and every member the row did not carry was lost - the `MethodOnly` and `Hidden` members
  the caller can neither see nor write, `ReadOnly` ones and visible siblings alike. The stored value of each record (or record
  array) attribute whose member a cell writes is now copied into the update first (`GraphQL/Mapping/QueryRowRecordMerge`, without
  Secret members, which the engine carries over), so a cell changes exactly its path. Record array elements are addressed by
  position and changed in place (with or without a record key; before, such a cell failed with `MAPPING004`). An explicit value
  for a protected member is still refused with `ATTRIBUTE_NOT_WRITABLE`. Tests `UnitTests/GraphQL/QueryRowRecordMergeTests`,
  `IntegrationTests/GraphQL/CkV2/QueryRowRecordMemberPreservationTests` (test record `AccessTestKeyedDetail`).
- AB#8355 Refactor: `PersistedQueryLookup` (AB#8273) delegates to the engine's `GetActiveRtEntityByRtIdAsync`, the same rule the
  mesh-adapter node `GetQueryById@1` and the MCP persisted-query tools use now. Behaviour unchanged.
- AB#8331 New: repair of an active computed column. `POST streamdata/archives/{archiveRtId}/computed-columns/{name}/repair?from=&to=`
  and the GraphQL mutation `repairComputedColumn(rtId, name, from, to)` queue a recompute job that re-evaluates the rows whose
  cell is NULL although an operand of the formula is not NULL (default range: first row to now); the column stays Active, the job
  appears in the recompute jobs with `computedColumnName`. Additive (new endpoint and mutation). With the engine of AB#8331 a
  computed-column backfill no longer runs out of memory on multi-million-row archives (mXparser formula parsed once per thread
  instead of per row) and an evaluation aborted by an exception is never stored as NULL: ingest fails the write, the backfill retries
  the page once and then fails its job with the affected time slice in the reason. With mXparser 4.4.2 (BSD-2, engine change of AB#8364) there is no license warning.
- AB#8273 Fix: a deleted persisted query no longer runs. The default delete archives a query entity (`rtState = Archived`), and
  the lookup by id returned it anyway, so `streamData.streamDataQuery(rtId)`, `runtime.runtimeQuery(rtId)` and the query-row
  mutations `runtimeQuery(rtId) { create / update }` kept working for anyone who knew the rtId while the query lists no longer
  showed it (found on test-2 `energyiq`). They now answer the not-found error of a query that never existed
  (`RtQuery with id '…' not found` / `Query with id '…' not found`, code `GraphQlModelValidationErrors`) and no row. There is no
  query-definition cache: every execution reads the stored query, so an update is visible at once on every replica.
  `GraphQL/Utils/PersistedQueryLookup`; tests `UnitTests/GraphQL/PersistedQueryLookupTests`,
  `IntegrationTests/StreamData/DeletedPersistedQueryTests`.
- AB#8275 New: the REST rollup list `GET streamdata/archives/{archiveRtId}/rollups` returns `referenceTimeZone` and
  `dayStartOffsetMs` per rollup, with the same values as GraphQL `rollupsFor` (null when the rollup has none). Additive JSON
  fields; the other fields and the response shape are unchanged. SDK clients read them once `RollupArchiveInfoDto` carries them
  (octo-sdk change of AB#7362); then octo-cli `ListRollupsForArchive` and MCP `list_rollups_for_archive` can show a gas-day
  rollup (`Europe/Vienna`, `21600000` = 06:00).
- AB#7361 New: gas days through GraphQL. `createRollupArchive` takes the optional `dayStartOffsetMs` (local day start of a
  `CALENDAR_DAY` rollup in ms after local midnight in `referenceTimeZone`, e.g. `21600000` = 06:00); the engine validates it at
  create time and an invalid value (outside `[0, 86400000)`, not a whole minute, another alignment, a chained rollup with another
  day start or zone) is refused as STREAMDATA_ERROR naming `DayStartOffsetMs`; an unknown `referenceTimeZone` now answers
  a message naming the field instead of the masked "An error occurred". `rollupsFor` returns `dayStartOffsetMs`.
  The transient `downsampling` query takes `timeZone`, `binInterval` (whole days) and `binOrigin` (`HH:mm`): with any of them the
  bins are local days (23/24/25 h across DST, cut like `CALENDAR_DAY` rollups) and `limit` is no longer required (it became
  optional; without the new arguments it is still required). `resolveSeriesQuery` takes `dayStartOffsetMs` or `binOrigin` and sets
  the engine's `QueryDayStartOffsetMs`, so a gas-day request is answered from the gas-day rollup and a midnight request never from
  gas days (before, every request counted as a midnight request). Invalid arguments are refused naming the argument. The schema
  change is additive (new optional fields and arguments, `limit` relaxed from `Int!` to `Int`). Persisted `RtDownsamplingSdQuery`
  keeps its shape. Raw-archive local-day bins need the engine-mongodb package of AB#7360.
- AB#8228 Fix: the asset repository recovers without a restart after a tenant's MongoDB user was dropped and re-created. A request that
  fails with a MongoDB authentication error (error 13 / 18, failed SCRAM) evicts the tenant's cached MongoDB clients, at most once per 15 s and
  tenant, so the next request authenticates anew. GraphQL errors never reach the HTTP pipeline and resolvers map their own exceptions, so the
  eviction is called from the central error mapping (`HandleException`) and from the unhandled-exception hook.
  Needs the common-services package of AB#8228 (`TenantDatabaseUserHealer.EvictOnAuthenticationFailureAsync`).
  Round 3: the CK model observability sweep (background read per tenant) evicts on an authentication failure too; REST controllers that answer
  their own 500 (e.g. `ModelsController`) are covered by the common-services result filter (`TenantPoolEvictionResultFilter`).
- AB#8168 Fix: an aggregation with several STATE_DURATION columns on the same attribute (for example state 0 and state 100) no longer
  fails with ASSET1002. A single STATE_DURATION column keeps the result key `<attribute>_stateduration`; when the same attribute has
  several, each key carries its comparison value (`position_stateduration_0`, `position_stateduration_100`; rule:
  `StreamDataAggregationOutputNaming` in the engine: the comparison value is trimmed and lower-cased, ASCII letters and digits are kept,
  `-` becomes `neg`, every other character becomes `_`, runs of `_` are collapsed and leading/trailing `_` removed, a value with nothing left
  becomes `x`; `low  power` gives `low_power`, `-1.5` gives `neg1_5`).
  Clients that read the result by key must use that key for those columns. Needs the engine package of AB#8168.
- AB#7812 New: Stream-data queries return attributes of the anchor entity of a row as columns. A column `anchor.<attributePath>`
  in the column list of a simple stream-data query (persisted `SimpleSdQuery` and transient `simple`, on raw, time-range and
  rollup archives) shows an attribute of the entity the row's `rtId` points at, for example the OBIS code and the unit of an
  energy measurement. Enum values come as names (`resolveEnumValuesToNames`), the anchors of one page are read in one query with
  the caller's data permissions (a hidden or missing anchor gives null cells), and an unknown path is refused with an error that
  names the column and the CK type. Display only in v1: an anchor column in a filter, sort order, aggregation, grouping or
  downsampling query (also a downsampling mode on `rows`) is refused, and Hidden and Secret attributes of the anchor cannot be shown. The value is the current value of the anchor. No CK model, CrateDB or rollup change; queries
  without anchor columns behave as before.
