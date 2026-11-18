---
description: StreamData query surface, mutations and setup: persisted and transient queries, columns, anchors, gas-day bins, archive and rollup methods.
applies_to: **/GraphQL/StreamData*.cs, **/Types/StreamData*.cs, **/StreamData/Methods/**, **/PersistedQueryLookup.cs, **/DataPermissionStreamGuard.cs
---

# Stream data in GraphQL

## StreamData Query Surface

Three execution shapes, all archive-driven (per CK Archive snapshot, which carries the target CK type and the captured column list):

| Entry point | Use case |
|---|---|
| `streamData.streamDataQuery(rtId)` | Execute a persisted `RtStreamDataQuery`. Loaded entity holds `ArchiveRtId`, columns, filters, sort, time range. The resolver dispatches to the correct repo method based on the loaded subtype (`RtSimpleSdQuery` / `RtAggregationSdQuery` / `RtGroupingAggregationSdQuery` / `RtDownsamplingSdQuery`). The query is loaded through `GraphQL/Utils/PersistedQueryLookup`: a deleted (archived) query answers not found like a missing one; the lookup by id alone returns archived entities. The helper delegates to the engine's `RuntimeRepositoryExtensions.GetActiveRtEntityByRtIdAsync` (Runtime.Contracts), which the mesh adapter (`GetQueryById@1`) and the MCP persisted-query tools use too. Same for `runtime.runtimeQuery(rtId)` and the query-row mutations. No query-definition cache: every call reads the stored entity. |
| `streamData.transientStreamDataQuery` | Ad-hoc execution without persistence. Four sub-connections: `simple`, `aggregation`, `groupingAggregation`, `downsampling`. The server derives `ckTypeId` from the archive snapshot (no client argument). All four resolvers collect CK query columns with `IgnoreNavigationProperties = true` — archive tables only carry physical columns, and unbounded navigation expansion over a densely connected CK model explodes combinatorially (observed as a >60 GB runaway allocation that killed the service; the engine additionally enforces `CkTypeQueryColumnOptions.MaxColumns` as a fail-fast cap). |

For the same reason, the CK metadata connection `constructionKit.types.availableQueryColumns`
(`CkTypeDtoType.ResolveAvailableQueryColumns`) defaults `maxDepth` to **1** when navigation
properties are included and no explicit depth is given — one navigation level instead of
unbounded traversal, so column pickers keep working on densely connected models instead of
tripping the `MaxColumns` cap and erroring with an empty list. Deeper traversal must be
requested explicitly via the `maxDepth` argument (still backstopped by `MaxColumns`).

Both surfaces accept runtime overrides on the `rows` / sub-connection level:

- `arg: StreamDataArguments` — override time range and limit
- `sortOrder: [Sort]` — override sort order
- `fieldFilter: [FieldFilter]` — **additional** filters, **AND-combined** with the persisted `FieldFilter` server-side (`StreamDataQueryDtoType.MergeFilters`). Same merging pattern as the runtime `RtQuery.Rows.fieldFilter` argument.

The persisted query's `aggregations` sub-connection takes a `ResultAggregationInput` (count/min/max/avg/sum × attribute paths) and runs a second engine call over the same data set as `rows` — useful for "give me the avg/max alongside the rows" without persisting an aggregation-typed query.

**Column types (`columns { attributeValueType }`).** Persisted and transient queries build their column metadata in
`GraphQL/StreamDataColumnBuilder`. The path is matched against the CK type's query columns **case-insensitively** (exact match first),
the same rule as the query execution (`StreamDataFieldResolver`, `OrdinalIgnoreCase`): a stored PascalCase path (`ActivePower`, EnergyIQ seed)
gets the CK value type (`DOUBLE`) — before, the exact match failed and the column was `STRING`, so MeshBoard numeric pickers stayed empty.
A path that is no CK attribute but a field of the archive (default fields `timestamp`/`rtId`/`window_start`…, `row_origin`…, rollup and
computed columns) keeps `STRING` as before. A path that exists nowhere is reported as `attributeValueType: null` — explicitly unknown, for
every aggregation function — via the `UnknownStreamDataColumn` marker that `RtQueryColumnType` resolves to null. The field was already
nullable, so the schema is unchanged and no enum value was added. The persisted resolver loads the archive snapshot for this (field resolver).
Tests: `UnitTests/GraphQL/StreamDataColumnBuilderTests`, `IntegrationTests/StreamData/StreamDataColumnTypeTests`.

**Anchor columns.** A column `anchor.<attributePath>` in the column list of a **simple** stream-data query (persisted `SimpleSdQuery`,
transient `simple`; raw, time-range and rollup archives) shows an attribute of the row's anchor entity (the entity its `rtId` points at, e.g. the
OBIS code and the unit of an energy measurement). Code: `GraphQL/StreamDataAnchorColumns` (`Plan` splits the columns before the repository is
called, so the engine validator and the SQL are untouched, and asks the engine for `rtid` when it is not listed; `Validate` checks the path against
the archive's target CK type: unknown = `ASSET1004` naming column and type, Hidden = `ATTRIBUTE_NOT_QUERYABLE`, Secret refused; `BuildRowsAsync`
loads the distinct anchor ids of ONE page with ONE `GetRtEntitiesByIdAsync` through the request session = the caller's rights, reads the values with
`RtPathEvaluator.GetValue` and puts them into the row values under the requested column; an anchor the caller may not read or that does not exist
gives null cells). Enum names come from the existing cell resolver through `ColumnNameMapping.EnumId` of the attribute path
(`resolveEnumValuesToNames`). **Display only in v1:** `StreamDataFieldValidation` and the explicit `StreamDataAnchorColumns.EnsureNone` calls refuse an
anchor column in filters (all operators), sort orders (also runtime overrides), aggregations, grouping, downsampling (also a simple query run in
downsampling mode, at query level `arg` and at `rows(arg:)` level, persisted and transient) and the `aggregations` sub-connection with a message that names the column and says "display-only". The value is the
**current** value of the anchor, not the value at the time of the measurement. Protected attributes: Secret attributes and records (also whole-record paths such as `anchor.<record>` and record members such as `anchor.<record>.<secret>`) are not CK query columns, so they are refused as "not an attribute"; Hidden = `ATTRIBUTE_NOT_QUERYABLE`; covered transient and persisted with canary values by `StreamDataAnchorColumnsTests` (no response contains a protected value). The type gate of the archive's own type
(`DataPermissionStreamGuard`) still decides the stream rows; anchors of a type derived from the archive's type are judged by their own policies.
MCP `execute_stream_data_query` / `query_stream_data_simple` call the stream-data repository directly (not GraphQL) and do not support `anchor.*`
columns (the engine refuses them as unknown columns); octo-cli has no stream-data query command (only the generic GraphQL command, which inherits it).
Tests: `UnitTests/GraphQL/StreamDataAnchorColumnsTests` (split, order, refusal, one load per page, null cells),
`IntegrationTests/StreamData/StreamDataAnchorColumnsTests` (fixture `AnchorColumnsStreamDataFixture`: rollup, raw, time-range, persisted and transient,
enum names, data policy on a derived anchor type, unknown path, display-only matrix).

**Integral.** `aggregationType: INTEGRAL` (transient `aggregation` / `groupingAggregation`) and the CK
value `System/AggregationTypes.Integral` (key 7, System 2.8.0) on persisted `AggregationSdQuery` / `GroupingAggregationSdQuery` give the step
integral over `[from, to)` in value unit x hours, result cell `<attr>_integral`, column `aggregationType: INTEGRAL`, value type `DOUBLE`
(mappings: `StreamDataGraphQlMapper`, `StreamDataTransientQuery`, `StreamDataQuery`, wire suffix in `StreamDataFieldResolverExtensions`). The
persisted column mapping now also reports `TIME_WEIGHTED_AVERAGE` / `STATE_DURATION` (they reported `NONE` before). `StreamDataIntegralValidation`
refuses, with `ASSET1004` and the column named, an integral on a non-numeric column (CK value type other than Integer/Integer64/Double, or a
built-in or platform field; computed columns pass when their result type is numeric) and any integral in a downsampling query; a missing From/To is the engine's structured
`StreamDataException`. Raw archives only (rollups: not supported). Tests: `StreamData/StreamDataIntegralQueryTests`.

**Gas days / local-day bins.** `createRollupArchive.dayStartOffsetMs` (validated by the engine
at create time) and `rollupsFor.dayStartOffsetMs` (REST `archives/{id}/rollups` carries `referenceTimeZone`/`dayStartOffsetMs` from the same
rollup snapshot); transient `downsampling(timeZone, binInterval, binOrigin)` builds local-day bins
(`GraphQL/StreamDataLocalDayBins`: parses `binOrigin` `HH:mm`, checks `binInterval >= 1`, runs `DownsamplingBinAxis.Resolve` before the
repository call; `limit` is required only without these arguments; `groupByColumnPaths: ["rtId"]`
(`GraphQL/StreamDataDownsamplingGroupColumns`) gives one row per series and bin with the cell `rtId` after `timestamp` (per-series empty bins only with local-day bins, engine cap
100,000 bins x series rows; fixed-width empty bins stay one row with a null `rtId`), other paths
are refused because the engine's downsampling row carries only the rtId; `StreamDataTransientUserContext.GroupColumnsInRows` keeps
the simple-downsampling cells unchanged); `resolveSeriesQuery(dayStartOffsetMs | binOrigin)` sets
`SeriesResolutionRequest.QueryDayStartOffsetMs` in `StreamDataQuery.BuildSeriesResolutionRequest` — without it a gas-day rollup is never
chosen under PerQuery. Persisted downsampling queries and `GetQueryByIdNode` (mesh adapter) carry no day start (midnight). Tests:
`UnitTests/GraphQL/StreamDataLocalDayBinsTests`, `ResolveSeriesQueryDayStartTests`, `GasDaySchemaTypesTests`,
`IntegrationTests/StreamData/GasDayGraphQlTests`, `DownsamplingGroupByGraphQlTests`, `UnitTests/GraphQL/StreamDataDownsamplingGroupColumnsTests`.

GraphQL types are dynamically created based on the tenant's construction kit model stored in MongoDB.

## StreamData Mutations

Custom (non-generated) mutations on `streamData` for archive and rollup lifecycle. The
generic CkEntity-typed `runtime.systemStreamDataCkArchives.create` / `…CkRollupArchives.create`
mutations still exist (auto-generated from the CK model), but the rollup variant should not
be used directly — see `createRollupArchive` below for why.

| Field | Returns | Notes |
|---|---|---|
| `activateArchive(rtId)` / `disableArchive` / `enableArchive` / `retryArchiveActivation` | `ArchiveTransitionResult` | Status transitions on `IArchiveLifecycleService`. Polymorphic — work on both `CkArchive` and `CkRollupArchive`. |
| `deleteArchive(rtId)` | `Boolean` | Drops the CrateDB table and soft-deletes the entity. Refuses if active rollups still reference a raw archive (concept §6 / `RollupSourceInUseException`). |
| `createRollupArchive(input)` | `OctoObjectId` | **Server-side rollup creation.** Input carries only the rollup-specific fields (`sourceArchiveRtId`, `bucketSizeMs`, `watermarkLagMs`, `aggregations[]` + optional name); `TargetCkTypeId` is inherited from the source archive and `Columns` is derived from the aggregations via `RollupColumnGenerator` server-side. Single source of truth for the column-derivation rule — clients no longer mirror it. Requires `StreamDataAdmin`. |
| `freezeRollupArchive(rtId, until)` / `unfreezeRollupArchive(rtId, acceptGaps)` / `rewindRollupWatermark(rtId, toBucketEnd)` | `ArchiveTransitionResult` | Rollup-only lifecycle. All require `StreamDataAdmin`. |

**Archive lifecycle as CK methods.** `activateArchive`, `disableArchive`, `enableArchive`, `retryArchiveActivation`,
`deleteArchive`, `createTimeRangeArchive` and `createRollupArchive` are **deprecated** (`@deprecated(reason: "Use <method id>")`) and keep
working unchanged; each is replaced by a method of System.StreamData 1.24.0, called through the typed method mutations
(`runtime { systemStreamDataRawArchives { activate(rtId) { result { archiveRtId status transition } } } }`, `systemStreamDataRollupArchives { createArchive(input: { archive: {...} }) { result } }`):

| Mutation | Method | Handler |
|---|---|---|
| `activateArchive` | `System.StreamData/Archive.Activate-1` | `ArchiveActivateHandler` |
| `disableArchive` | `System.StreamData/Archive.Disable-1` | `ArchiveDisableHandler` |
| `enableArchive` | `System.StreamData/Archive.Enable-1` | `ArchiveEnableHandler` |
| `retryArchiveActivation` | `System.StreamData/Archive.RetryActivation-1` | `ArchiveRetryActivationHandler` |
| `deleteArchive` | `System.StreamData/Archive.DeleteArchive-1` | `ArchiveDeleteArchiveHandler` |
| `createTimeRangeArchive` | `System.StreamData/TimeRangeArchive.CreateArchive-1` | `TimeRangeArchiveCreateArchiveHandler` |
| `createRollupArchive` | `System.StreamData/RollupArchive.CreateArchive-1` | `RollupArchiveCreateArchiveHandler` |
| `freezeRollupArchive` / `unfreezeRollupArchive` / `rewindRollupWatermark` | `System.StreamData/RollupArchive.Freeze-1` / `Unfreeze-1` / `RewindWatermark-1` (1.25.0) | `RollupMethodHandlers.cs` |
| `recomputeArchive` / `backfillRollupFromSource` | `System.StreamData/RollupArchive.Recompute-1` / `BackfillFromSource-1` (1.25.0) | `RollupMethodHandlers.cs` |
| `addComputedColumn` | `System.StreamData/Archive.AddComputedColumn-1` (1.28.0) | `ArchiveAddComputedColumnHandler` |
| `addArchiveColumn` | `System.StreamData/Archive.AddColumn-1` (1.28.0) | `ArchiveAddColumnHandler` |
| `removeComputedColumn` | `System.StreamData/Archive.RemoveComputedColumn-1` (1.28.0) | `ArchiveRemoveComputedColumnHandler` |
| `updateComputedColumnFormula` | `System.StreamData/Archive.UpdateComputedColumnFormula-1` (1.28.0) | `ArchiveUpdateComputedColumnFormulaHandler` |
| `retryComputedColumnBackfill` | `System.StreamData/Archive.RetryComputedColumnBackfill-1` (1.28.0) | `ArchiveRetryComputedColumnBackfillHandler` |
| `repairComputedColumn` | `System.StreamData/Archive.RepairComputedColumn-1` (1.28.0) | `ArchiveRepairComputedColumnHandler` |

**Lifecycle and rollup mutations are method facades.** The eight lifecycle and rollup mutations keep their role check and
call their method through `IArchiveMethodFacade` (as the REST endpoints, `docs/stream-data-management.md`); the answer keeps the
mutation's shape and `STREAMDATA_*` codes (`METHOD_TARGET_NOT_FOUND` becomes `STREAMDATA_ARCHIVE_NOT_FOUND`), new refusals are
the method's `METHOD_FORBIDDEN`. With `Methods:StreamData:Enabled=false`, or in a tenant whose CK model does not declare the method, they keep the direct call.

Since System.StreamData 1.28.0 (AB#8600) **every** field of `StreamDataMutations` is deprecated with its method id. The unit test
`ArchiveMethodTests.EveryStreamDataMutation_IsDeprecated_WithTheMethodThatReplacesIt` fails for a new streamData mutation without a
method row in `ArchiveMethodHost`.

- Code: `StreamData/Methods/` (`ArchiveMethodHost`: the table above, `AddArchiveCkMethodHandlers`, deprecation reasons; `ArchiveLifecycleMethodHandlers`,
  `ArchiveCreateMethodHandlers`, `ArchiveMethodSupport`). Registered in `Program.cs` (not in `GatewayOwnedMethodHandlers`, so test hosts without stream data
  are not affected), in-process (`AddCkInProcessMethodHandlers`). Switch `Methods:StreamData:Enabled` (`OCTO_METHODS__STREAMDATA__ENABLED`, default true):
  off = no handler, a method call answers `METHOD_HANDLER_NOT_AVAILABLE`, the mutations keep working.
- **Same semantics as the mutation.** Each handler calls the same engine service (`IArchiveLifecycleService`, `ITimeRangeArchiveRuntimeStore.InsertAsync`,
  `IRollupArchiveLifecycleService.CreateAsync`) and returns the same shape. Authorization: role `StreamDataAdmin` (declared in the model, enforced by the gateway and the
  pipeline: `METHOD_FORBIDDEN` instead of the mutation's `FORBIDDEN`). Errors: a `StreamDataException` becomes the declared failure with **the mutation's code**
  (`ArchiveMethodErrors.CodeOf`, the mapping of `HandleStreamDataException`) and the parameters of its extensions (`archiveRtId`, `fromStatus`, `attemptedTransition`,
  `actualStatus`, `path`) plus `message`. A tenant without stream data answers `STREAMDATA_NOT_ENABLED` (the mutation: generic `ASSET1002`). Input errors that the mutation
  answers with its own validation message (`createRollupArchive` source rules, unknown time zone) are `METHOD_VALIDATION_FAILED` naming the field (`archive.sources`,
  `archive.referenceTimeZone`). A target that is no archive of the call's tenant is `METHOD_TARGET_NOT_FOUND` (gateway).
- **Caller tenant (central).** An in-process call is not re-validated by a bus host, and the HTTP tenant gate may run in LogOnly
  (locally a token of `octosystem` could disable an archive of a child tenant). The in-process transport of octo-common-services refuses such a
  call for **every** in-process method before any handler runs (`CkMethodCallerTenantFilter`, band 150, rule `CkMethodCallerTenantRule` of the
  bus host): a user token (`sub`) must carry the call's tenant in `tenant_id`; a service token (no `sub`) must carry it too or be on
  `TenantAuthorization:CrossTenantServiceClientIds`. Otherwise `METHOD_FORBIDDEN` with `reason = tenant-not-allowed`, nothing touched. The
  archive handlers have no tenant check of their own any more (the stopgap `ArchiveMethodCallerTenantGuard` is removed). The
  mutations keep their behaviour (they follow the HTTP gate).
- **Rollup control methods (System.StreamData 1.25.0).** `RollupMethodHandlers.cs`: `RollupArchive.Freeze-1` /
  `Unfreeze-1` / `RewindWatermark-1` run the `IRollupArchiveLifecycleService` call of the mutation and answer
  `ArchiveTransitionResult` (status read back from the rollup store, `RollupTransitions.RunAsync`); `Recompute-1` calls
  `IRecomputeOrchestrator.RecomputeArchiveAsync` (trigger Manual, the staging generation + pointer swap in the call; a
  missing or non-activated rollup is a job in state `Failed`, as with the mutation; an invalid `rtIdScope` is
  `METHOD_VALIDATION_FAILED`), `BackfillFromSource-1` calls `EnqueueBackfillFromSourceAsync` and answers its job: a new Pending
  job, the already active job of the rollup the backfill was folded into, a `Failed` job for a missing or non-activated rollup, or
  no result for an empty source. A target id the caller cannot see is `METHOD_TARGET_NOT_FOUND` from the gateway, before any job. Both stay synchronous; async methods are a later decision. The five mutations are deprecated
  with the method id.
- **Column methods (System.StreamData 1.28.0, AB#8600).** `ColumnMethodHandlers.cs`: the six instance methods of `Archive` call the
  `IArchiveLifecycleService` member of the mutation they replace and answer its shape. `AddComputedColumn-1`, `AddColumn-1`,
  `RemoveComputedColumn-1` and `UpdateComputedColumnFormula-1` answer `ArchiveTransitionResult` (transition names as the mutations:
  `AddComputedColumn`, `AddArchiveColumn`, `RemoveComputedColumn`, `UpdateComputedColumnFormula`; `ArchiveTransitions.RunAsync`);
  `RetryComputedColumnBackfill-1` and `RepairComputedColumn-1` answer the queued job as `RecomputeJobInfo` with `computedColumnName`
  (an already queued job of the column as is; no result when no job store is wired). The backfills and repairs stay background
  jobs (AB#8002): the queuing writes run under the host's stopping token, not the call's, exactly as `StreamDataMutation.HostToken`,
  so a caller that gives up cannot leave a half-queued column. `resultType` is the CK enum `CkComputedColumnResultType`
  (`DOUBLE`, ...), mapped to the engine enum by name; an undefined number is `METHOD_VALIDATION_FAILED` on `resultType`. A
  path that reaches a Hidden attribute (AB#6247) is refused by the engine like the mutation: `STREAMDATA_ERROR` with the engine
  message; a rollup refuses the column changes (`STREAMDATA_PATH_INVALID` for `AddColumn-1`, as the mutation). An unknown
  computed column in update, retry or repair, which the engine refuses with a plain `InvalidOperationException` (the
  mutations answer their generic error), is `STREAMDATA_ERROR` with the engine message (`ArchiveColumnChanges.RefusingUnknownColumnsAsync`), not the
  undeclared `METHOD_HANDLER_FAILED`. The shared helpers log the invocation id and the error code, never the target id or the
  engine message.
- **Hidden columns: reason, readable history, redaction (System.StreamData 1.27.0).** When a CK model change makes an
  archive column reach an attribute with `access: Hidden`, the engine sets the archive to `Failed` and stores why
  (`Archive.StatusReason`) and which columns (`Archive.HiddenColumns`); both are runtime state and appear in the runtime
  GraphQL query of the archive types. Such an archive refuses writes, but the stream-data queries still answer its history
  without the Hidden columns (a Hidden column named in a query is refused as before). `Archive.RedactHiddenColumns-1`
  (`ArchiveRedactHiddenColumnsHandler`, no mutation) is the way back and is **destructive**: it drops these columns and
  their stored values from the CrateDB table and the archive definition, records it in the audit trail and activates an
  archive that was Failed on them again. It needs `StreamDataAdmin` and `input: { confirmDataLoss: true }`; otherwise
  `STREAMDATA_CONFIRMATION_REQUIRED` and nothing changes. A rollup that aggregates one of the columns blocks it with
  `STREAMDATA_REDACTION_BLOCKED` (parameters `columns`, `rollupRtIds`). Without a Hidden column it drops nothing
  (`removedColumns: []`). The handler logs no target id. A model import in a service without stream data reaches the archives
  through the tenant-update event: this service re-checks them after its tenant setup (octo-common-services). Rules and
  background: the octo-construction-kit-engine page "Archives and Hidden attributes" (archive-hidden-columns). Tests: `UnitTests/StreamData/Methods/ArchiveRedactHiddenColumnsHandlerTests`,
  `IntegrationTests/StreamData/ArchiveLifecycleMethodTests.Redaction`.
- **Redaction with the confirmed columns (System.StreamData 1.29.0).** `Archive.RedactHiddenColumns-1` drops whatever is
  Hidden when it runs, also columns that became Hidden after the caller showed the list to the administrator; it is
  **deprecated** and removed in the next major of System.StreamData. Interactive callers use `Archive.RedactHiddenColumns-2`
  (GraphQL field `redactHiddenColumns2`, `ArchiveRedactHiddenColumns2Handler`, no mutation), with the same role, confirmation,
  result and errors plus the **required** `expectedColumns`: the column paths the administrator confirmed. It drops only when
  they are exactly the Hidden columns at execution time (ignoring case, order and duplicates); otherwise it answers
  `STREAMDATA_REDACTION_COLUMNS_CHANGED` with the parameters `expectedColumns` and `currentColumns` (comma-separated) and
  nothing is dropped or audited: read the archive again and confirm the current set. `expectedColumns: []` confirms that no
  column is Hidden. Example:
  `redactHiddenColumns2(rtId: "...", input: { confirmDataLoss: true, expectedColumns: ["Pin"] }) { result { status removedColumns } }`.
  A repeat after a success finds no Hidden column and is refused; the method is declared not idempotent, so a retry with an
  idempotency key gets the stored answer. Tests: `UnitTests/StreamData/Methods/ArchiveRedactHiddenColumns2HandlerTests`,
  `IntegrationTests/StreamData/ArchiveLifecycleMethodTests.Redaction` (`RedactHiddenColumns2_*`); the drop against CrateDB is
  tested in octo-construction-kit-engine-mongodb.
- **System tenant import.** The startup validation of in-process registrations looks the methods up in the system tenant's CK cache, which holds System.StreamData only
  when stream data was enabled there. `SystemTenantStreamDataModelImport` (Order 12, after the default configuration, before the validation at 15) imports the embedded model
  into the system tenant (idempotent, downgrade guard). It does not enable stream data for the system tenant.
  **It leaves the system tenant's CK cache loaded with the model.** The validation (`CkCacheMethodDefinitionSource`) reads only a
  cache that is already loaded, while the import unloads it when the model set changed (this replica imported) or the loaded cache lacks the
  installed version (another replica imported first). Before this fix nothing loaded it again before Order 15, so on the first rollout
  with several replicas a pod stopped with "the CK cache of tenant(s) 'octosystem' has no such method". The import step now loads the cache
  after the import and checks that it holds System.StreamData (by name, embedded version or newer); a cache loaded concurrently from the
  state before the import is unloaded and loaded again, a load that ran out waiting for another replica's import (exception marked
  `CkModelImportInProgress`) is loaded again, a cache unloaded during the check counts as not holding the model (at most
  `MaxCacheLoadAttempts` = 3 loads; any other load failure fails the step). A model that is really not installed (import
  skipped for missing dependencies) is logged WARN and still fails the validation. Tests: `IntegrationTests/StreamData/SystemTenantStreamDataImportReplicaTests`
  (fixture `AssetRepoReplicaFixture`: several replicas with their own service provider on one fresh system tenant, started together).
- **Create parameters are the generated records.** The handlers bind `TimeRangeArchiveCreateArchiveParameters` / `RollupArchiveCreateArchiveParameters`
  of System.StreamData directly; the record arrays (`Columns`, `Sources`, `Aggregations`, `IAttributeValueList<T>`) are bound by `CkMethodJson.Options` of
  octo-common-services (the plain look-alike classes are removed). A missing optional `sources` is `null`, an explicit `[]` an empty list, so the
  source rules of the mutation stay as they were. The generated CK enums (`RtCkRollupFunctionEnum`, `RtBucketAlignmentEnum`) become the engine enums by name
  (`ArchiveMethodSupport.ToEngine`).
- Tests: `UnitTests/StreamData/Methods/ArchiveMethodTests` (code mapping, host table, switch, source rules, binding from the wire JSON, enum mapping), `IntegrationTests/StreamData/ArchiveLifecycleMethodTests`
  (fixture `ArchiveMethodStreamDataFixture`: MongoDB + CrateDB, real schema, gateway, pipeline and the production registration; per method the same transition as the
  mutation side by side, METHOD_FORBIDDEN without the role for all seven, the invalid-transition code, target outside the tenant, deprecation reasons in the schema;
  `ArchiveLifecycleMethodTests.Columns` (AB#8600): each column method side by side with its mutation, the Hidden-attribute refusal, the rollup refusal,
  METHOD_FORBIDDEN for all six and a token of another tenant).

All custom mutations use `ResolveConnectionContextExtensions.HandleException` to surface
domain exceptions (`InvalidArchiveStateTransitionException`, `ArchiveNotFoundException`,
`RollupSourceMissingException`, …) as stable GraphQL error codes with the underlying message
in `error.extensions.OctoDetails`.

## StreamData Setup

`Program.cs` registers `IStreamDataCkModelDescriptor` so the engine's
`EnsureStreamDataCkModelImportedAsync` auto-import path picks the shipped CK model version
instead of the hardcoded 1.0.0 fallback:

```csharp
builder.Services.AddSingleton<IStreamDataCkModelDescriptor>(
    _ => new StreamDataCkModelDescriptor(SystemStreamDataCkIds.CkModelId));
```

Any asset-repo deploy that ships a newer model (e.g. 1.0.0 → 1.1.0 adding `CkRollupArchive`)
auto-promotes the model on the next tenant resolve. Older sibling services without the
descriptor cannot downgrade the model on a tenant — the engine's downgrade guard
(`TenantContext.EnsureStreamDataCkModelImportedAsync`) skips when the installed version is
higher than the local descriptor's target.
