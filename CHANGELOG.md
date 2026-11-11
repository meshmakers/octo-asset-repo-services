# Changelog

## Unreleased

- AB#8778 New: blueprint update preview and result carry two more tenant-owned lists, `TenantOwnedRefreshed` (unedited
  tenant-owned seed entities that receive the changed seed) and `TenantOwnedUpdateAvailable` (edited ones the update keeps
  although the seed changed; subset of `TenantOwnedSkipped`) - REST `BlueprintUpdatePreviewDto` / `BlueprintUpdateResultDto`,
  GraphQL `previewUpdate` / `applyUpdate` (`tenantOwnedRefreshed`, `tenantOwnedUpdateAvailable`). Additive, empty by default.
  Needs octo-construction-kit-engine with AB#8778.
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
