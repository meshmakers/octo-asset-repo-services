---
description: ImportFromCatalog, LibraryStatus and CheckUpgrade, the tenant upgrade guard and its enforcement, dependents' ranges, file import and blueprints.
applies_to: **/UpgradeGuard/**, **/*CkUpgrade*.cs, **/Services/Ck*.cs, **/CkModelLibraryStatus*.cs, **/CkModelCatalogController.cs, **/BlueprintsMutation.cs
---

# CK model import from the catalog and the upgrade guards

## CK Model Import from Catalog (Tenant API)

Tenant-scoped endpoint to import a CK model directly from a catalog without file upload.

| Method | Endpoint | Auth Policy | Description |
|--------|----------|-------------|-------------|
| `POST` | `{tenantId}/v1/models/ImportFromCatalog` | DataModelManagementPolicy | Import CK model from catalog into tenant |

**Request body:**
```json
{
  "catalogName": "PublicGitHub",
  "modelId": "Energy-2.0.0",
  "forceBreakingUpdate": false
}
```

**Response:** `TransferModelResponseDto` with `jobId` for async tracking. `409 CK_UPGRADE_BLOCKED` when the model would break an installed
model and `forceBreakingUpdate` is not set (tenant upgrade guard, see below).

**Flow:** Fetches compiled model from catalog → serializes to JSON → caches in Redis → sends `ImportCkCommandRequest` through existing async job pipeline (RabbitMQ → Hangfire → `ITenantContext.ImportCkModelAsync`).

| `POST` | `{tenantId}/v1/models/ResolveDependencies` | ReadOnly | Resolve dependency tree for a catalog model against tenant |

**Request body:** Same as ImportFromCatalog (`catalogName` + `modelId`).

**Response:** `DependencyResolutionResponseDto` with recursive `RootModel` tree. Each item contains: `modelId`, `name`, `requiredVersion`, `installedVersion` (null if not installed), `action` ("install", "none"), and nested `dependencies`. Plus `isBlocked` / `blockers[]`: the flattened import set of the tree judged by the tenant upgrade guard, read-only (`ResolveDependenciesBatch` returns the same two members for `modelsToImport` as one set).

Installed state is judged by what the **parent model requires**: a classic
parent's compile-time exact pin stays exact (a newer installed version would make the parent `ResolveFailed`), a
range-retaining parent (`CkCompiledModelRoot.DependencyRanges`) accepts any installed version within its range and
≥ the floor, and no requirement is ever satisfied across majors. Actions: requirement met → `none`; a newer installed
version that does not meet it (or any unmet service-managed model) → `incompatible`, which blocks the batch import —
a newer installed version is never offered for a downgrade install (explicit downgrades go through `ImportCk`); an
older installed version → `install` (upgrade). The root model is judged as an exact pin. A dependency shared by
several parents is judged once per distinct requirement (re-review N8). `RequiredVersion` stays the pinned (floor)
version; `RequiredRange` (new, display only) carries the declared range + floor of a range-retaining parent, and the
batch tree correction works on the typed model id, never on the display strings (re-review N1). Installed versions come from
`ICkModelLibraryStatusService.GetInstalledModelVersionsAsync` (highest `Available` version per name).
`CkModelLibraryStatusService.CheckSystemCompatibilityAsync` still compares service-managed versions exactly.
It compares exactly only for classic (exact-pinned) catalog models; a range-retaining model is judged by its `dependencyRanges` (inside the range, at or above the floor). A System-managed dependency that has no `dependencyRanges` entry in a range-retaining model is a *transitive* pin (the flat `Dependencies` closure carries `System-2.7.0` via Basic, the ranges name direct dependencies only); it is skipped and NOT added to the shared `visited` set, so the recursion into the intermediate model still checks its own range (range-retaining intermediate) or exact pin (classic intermediate). The import dialog / `octo-cli ImportFromCatalog` path has the same rule in `CkImportDependencyResolver.ResolveDependencyTreeAsync`: for a range-retaining model a `Dependencies` entry without a `dependencyRanges` entry is transitive, is not judged at that level and is not added to `resolved` (the intermediate judges it with its own range/pin; classic models and the N8 requirement keying are unchanged; tests `TransitiveSystemPin_*` / `Batch_TransitiveSystemPin_*` in `ModelsControllerResolveDependenciesTests`). Tests: `CkModelLibraryStatusDependencyRangeTests` (`Transitive_System_*`) and `ModelsControllerImportTransitiveSystemPinTests` (single + batch import). Row-level proof for `octo-cli FixAll` (`CkModelLibraryStatusDependencyRangeTests`): a `ResolveFailed` classic model with a range-retaining catalog successor is `IsCompatible` + `NeedsAction`; a classic successor pinned to another System stays incompatible; the upgrade guard still removes `NeedsAction`.

### Tenant upgrade guard in LibraryStatus and CheckUpgrade

`GET {tenantId}/v1/models/LibraryStatus` and `POST {tenantId}/v1/models/CheckUpgrade` report, **read-only**, which installed
models an offered update would break (these two endpoints enforce nothing; the import endpoints do, see "Tenant upgrade guard enforcement" below). The service
`CkModelLibraryStatusService` calls `ITenantContext.EvaluateCkUpdateAsync` (engine-mongodb) with the compiled
catalog model of the offered version and the versions the catalogs offer; the guard reads the tenant database only.

- **LibraryStatus item** (`CkModelLibraryStatusItemDto`, all members optional for old clients): `isBlocked`, `blockers[]`
  (`CkUpgradeBlockerDto`: `dependent`, `update`, `kind`, `subject`, `code`, `message`, `dependentIsInUpdateSet`,
  `suggestedDependentUpdate`, `resolveHint`) and `isRangeRetaining` (installed model has `dependencyRanges`). A blocked row keeps
  `hasUpdate = true` and gets `needsAction = false` (so a client that predates the guard, for example octo-cli `FixAll`, skips it;
  the `ModelsNeedingActionCount` excludes it). The guard runs only for an installed, non-service-managed row with an update that is
  otherwise importable (compatible with the installed System models, no catalog inconsistency); catalog-only rows are never
  blocked. A guard failure for one row lands in that row's existing error state (`isCompatible = false`), never a 500.
- **A row is judged with the dependency updates its import pulls in**: `CkImportDependencyResolver` (extracted unchanged
  from `ModelsController`; `ResolveDependencies`/`ResolveDependenciesBatch` use the same code) resolves the dependency tree of the offered
  version against the installed `Available` models and flattens it to the import set; the row hands that set to the guard as ONE update
  set (`CkModelLibraryStatusService.ResolveUpdateSetAsync`), so row and import dialog cannot disagree (`Industry.Energy-3.6.0` brings
  `Basic-2.5.0`: the row is blocked with `Basic-2.5.0` as `update` and `EnergyCommunity.Registration-2.8.0` as `dependent`). It falls
  back to the offered version alone when that is not published or the dialog would not import it (incompatible branch). A row is still
  judged against the current installed state, not against the other rows. Cost: one guard evaluation (one tenant model read) per
  updatable row, also in the observability sweep; the dependency models come from the per-call `CatalogModelMemo` (each fetched once per
  status call) and the installed state is read once per call - no extra tenant lookups. The blueprint guard now expands its set the same way
  (see "Tenant upgrade guard for blueprints").
- **`resolveHint` is proven, not guessed**: when the guard suggests a newer catalog version of the blocking dependent
  (`suggestedDependentUpdate`, for example `EnergyCommunity.Registration-2.10.0`), the service evaluates the combined update set
  (offered model + that version) and keeps the suggestion (hint `update EnergyCommunity.Registration to 2.10.0 first`) only if no
  blocker for that dependent remains; otherwise both stay `null`. Only the newest offered version is tried.
- **CheckUpgrade** adds `isBlocked` and `blockers[]` to `UpgradeCheckResponseDto`; `hasBreakingChanges` stays the model's own
  migration verdict (it said "no breaking changes" for Registration 2.8.0 to 2.10.0 on staging-1 while dependents were the problem).
  The endpoint stays under `TenantAssetApiReadOnlyPolicy`: no job, no write. A guard failure is a 500 here (not a silent "not blocked").
- `message` is English and names the dependent (`EnergyCommunity.Registration 2.8.0 would break: exact pin Basic-2.4.0: Basic-2.5.0
  replaces it`); clients that localize render `code`, `dependent`, `update` and `subject`. `kind` is a string and may gain values.
- Tests: `CkModelLibraryStatusUpgradeGuardTests` (sbeg case, additive update, proven hint, error isolation, `isRangeRetaining`),
  `CkModelLibraryStatusUpdateSetTests` (dependency update blocks the row, safe combined set, satisfied dependency, no dependency updates, memo, row verdict == `ResolveDependencies` verdict over four states),
  `ModelsControllerCheckUpgradeTests` (blockers, read-only policy, no job, additive DTO shape / old payloads).

### Tenant upgrade guard enforcement

`POST {tenantId}/v1/models/ImportFromCatalog` and `ImportFromCatalogBatch` (octo-cli `ImportFromCatalog` and `FixAll` use the batch)
enforce the guard **before anything is cached or queued** (`ModelsController.EnforceUpgradeGuardAsync` ->
`ICkModelLibraryStatusService.EvaluateUpdateSetBlockersAsync`, which hands the whole set to `ITenantContext.EvaluateCkUpdateAsync`):

- **Update set** = the one model of `ImportFromCatalog`, or all `modelIds` of the batch, judged **as one set** (a dependent in the set is
  judged by its new version; hints are proven against the combined set). **Batch semantics: all or nothing** - a blocked set is refused
  as a whole (all models are compiled first, nothing is cached, no job queued, no partial import, no per-model result).
- **Refusal** = `409 Conflict`, `application/problem+json` (`UpgradeGuard/CkUpgradeBlockedError`, like the blueprint-lock contract):
  `type https://schemas.meshmakers.cloud/errors/ck-upgrade-blocked`, `code = CK_UPGRADE_BLOCKED`, `updateSet` (full ids as requested),
  `blockers[]` (`CkUpgradeBlockerDto`, language neutral) and an English `detail` that names the force option.
- **Override** = request member `forceBreakingUpdate` (default `false`, on both request DTOs; ignored by the read-only endpoints). Both
  endpoints are already under `DataModelManagementPolicy`, so a caller without it gets `403` before the flag is read. A forced import with
  blockers logs one WARN line (tenant, caller `sub` / `client_id` / `name`, update set, blockers) and proceeds; without blockers nothing
  is logged. A guard failure is a `500` even when forced (fail closed).
- **Not guarded on purpose**: the embedded System imports at service start (downgrade guard). `Models/ImportCk` is guarded
  by the file import guard (next section). Refinery Studio offers no force action; FixAll in octo-cli never sends the flag.
- `ModelsController` takes an `ILogger<ModelsController>` (audit line).
- Tests: `ModelsControllerUpgradeGuardEnforcementTests` (sbeg 409 with nothing cached/queued/written, additive update, forced + WARN
  audit line, batch with and without Registration, one blocked model refuses the batch, guard failure, policy attributes, additive DTOs),
  `CkModelLibraryStatusUpgradeGuardTests` (`EvaluateUpdateSetBlockersAsync`: set judged together, proven hint, empty set, failure).
- User documentation: octo-documentation `constructionKits/libraryManagement#upgrade-guard-enforcement`.

| `POST` | `{tenantId}/v1/models/CheckUpgrade` | ReadOnly | Pre-flight check for migration impact |

**Request body:** Same as ImportFromCatalog (`catalogName` + `modelId`).

**Response:** `UpgradeCheckResponseDto` with: `modelName`, `installedVersion`, `targetVersion`, `upgradeNeeded`, `migrationPathAvailable`, `hasBreakingChanges`, `errorMessage`, plus `isBlocked` and `blockers[]` (tenant upgrade guard, see above).

### Dependents' ranges and pins hold a version back

The engine guard (`EvaluateCkUpdateAsync`) treats a candidate of **another major** than the installed version as a parallel
install and does not judge it, but the import replaces the installed version: FixAll upgraded `Basic.Energy` 1.10.0 to
2.0.0 and `EnergyIQ` 2.14.0 (range `Basic.Energy-[1.9,2.0)`) became `ResolveFailed`. `Services/CkDependentRequirementGuard`
closes this for every version and every guard surface (it runs inside `EvaluateCompiledBlockersAsync`, so LibraryStatus rows,
`ResolveDependencies*`, `ImportFromCatalog*`, the blueprint guard and the file import all use it):

- A candidate whose name is installed `Available` in another version must lie inside the intersection of the requirements of
  every installed `Available` dependent the set does not replace, and of the models of the set (judged by their new
  requirements): range-retaining dependent = its declared range of the **direct** dependency, at or above the floor (a pin
  without a range entry is transitive, and is not judged); classic dependent = its exact pin. Service-managed
  candidates only for explicit callers (file import, CheckUpgrade). ResolveFailed dependents and candidates that are not
  installed are ignored, like in the engine guard.
- Blockers have the engine's shape: kind `RangeNotSatisfied` (`CK_UPGRADE_RANGE_NOT_SATISFIED`, subject = declared range) or
  `ExactPin` (`CK_UPGRADE_EXACT_PIN`, subject = the pin), message `EnergyIQ 2.14.0 would break: range Basic.Energy-[1.9,2.0)
  (floor 1.9.0) does not admit Basic.Energy-2.0.0`. A (dependent, update) pair the engine already reported is not repeated.
- **Single import past a range** is therefore the existing `409 CK_UPGRADE_BLOCKED` naming the dependent; `forceBreakingUpdate`
  (octo-cli `ImportFromCatalog --force…`) still overrides it with the WARN audit line. FixAll never forces.
- **LibraryStatus row**: when the newest version has a requirement blocker (`RangeNotSatisfied`, `ExactPin`, `FloorNotMet`),
  the row looks for the **highest allowed** catalog version (newer than installed, admitted by all installed dependents,
  compatible with the installed System models, its import set unblocked) and offers it in `catalogVersion` / `fullModelId` /
  `catalogName` with `needsAction = true` - FixAll imports it. Without one the row stays blocked on the newest version
  (`needsAction = false`). In both cases the new optional members `heldBackVersion` (newest version), `heldBackBy[]`
  (`CkUpgradeBlockerDto`: dependent + range/pin) and `heldBackReason` (`2.0.0 available, held back by EnergyIQ 2.14.0
  (Basic.Energy-[1.9,2.0))`, display only) say why. Cost: lower versions an installed dependent excludes are skipped without a
  guard call; each remaining candidate costs one guard evaluation, newest first, until one passes.
- Tests: `UnitTests/Services/CkDependentRequirementGuardTests` (pure check, rows, set evaluation, no duplicate pair),
  `UnitTests/TenantApi/ModelsControllerDependentRangeGuardTests` (409 naming the dependent, forced import, batch),
  `IntegrationTests/Tenant/DependentRangeGuardFlowTests` (real engine guard and tenant: range-retaining and exact-pinned
  dependents, nothing written).

### Tenant upgrade guard for the file import

`POST {tenantId}/v1/models/ImportCk` (a CK model file; octo-cli `ImportCk -f`) used to be unguarded ("explicit operator decision"): a System
file that removed an attribute used by `System.Identity` was accepted, left every model `Available` and made the tenant unloadable (no API
path back, `Delete` fails too). Decided 2026-10-18: the file import is guarded like every other upgrade guard surface.

- **Flow** (`ModelsController.ImportCk`): policy check of a requested force -> read the upload exactly as the import job does (`json`/`yaml`
  via `ICkSerializer`; a `zip` is opened for its `.json`, else its `.yaml`; unreadable -> `400`) -> `ICkModelLibraryStatusService.
  EvaluateFileImportBlockersAsync` -> `409 CK_UPGRADE_BLOCKED` **before anything is cached or queued**, else cache + queue as before.
- **Explicit caller**: the evaluation calls `ITenantContext.EvaluateExplicitCkImportAsync` (engine-mongodb), which, unlike the default
  `EvaluateCkUpdateAsync`, also judges service-managed models (`System`, `System.*`) and judges a **downgrade** like an upgrade (a harmless
  one is no blocker and stays allowed with the WARN of the engine; a breaking one is refused). Blueprints, FixAll, the batch import and the
  embedded import keep the default (System skipped).
- **Refusal** = the contract of the section "Tenant upgrade guard enforcement" above (`CkUpgradeBlockedError.CreateFileImportResult`: `409`, `application/problem+json`, `code`, `updateSet` =
  the model id of the file, `blockers[]`, plus `fileName`; the English `detail` names `forceBreakingUpdate` / `--forceBreakingUpdate`).
- **Override** = form member `forceBreakingUpdate` (default `false`). The endpoint stays on `TenantAssetApiReadWritePolicy`; a *requested*
  force needs `DataModelManagementPolicy` (checked via `IAuthorizationService`, `403` otherwise, nothing happens). A forced import with
  blockers logs the WARN audit line (tenant, caller, file name, model id, blockers); a guard failure is a `500` even when forced.
- **CheckUpgrade** (`EvaluateExplicitUpgradeBlockersAsync`) judges service-managed candidates too, so the preview agrees with the file import
  (`isBlocked` / `blockers` for System). `hasBreakingChanges` stays the migration view of the model itself.
- `ModelsController` takes `ICkSerializer` and `IAuthorizationService`; `ICkModelLibraryStatusService` gained `EvaluateFileImportBlockersAsync`
  and `EvaluateExplicitUpgradeBlockersAsync` (the existing members are unchanged).
- Tests: `ModelsControllerUpgradeGuardEnforcementTests` (ImportCk: 409 with nothing cached/queued, forced + WARN, 403 without the policy,
  additive/first install unchanged, unreadable file 400, fail closed, zip), `CkModelLibraryStatusUpgradeGuardTests` (explicit evaluation),
  `FileImportUpgradeGuardFlowTests` (real tenant: System file judged, default path skips it, nothing written, tenant loads).

### Tenant upgrade guard for blueprints

The tenant blueprint endpoints apply the guard to the CK models a blueprint installs or raises (`TenantApi/v1/Controllers/BlueprintsController`).
The logic is in the engine's `BlueprintService` (update set = the `ckModelDependencies` floors the tenant does not hold in a satisfying-or-newer
version, judged as ONE set before the first CK model is installed); this service supplies the evaluation: `Services/BlueprintCkUpgradeGuard`
(`IBlueprintCkUpgradeGuard`, registered in `RuntimeEngineBuilderExtensions`) delegates to
`ICkModelLibraryStatusService.EvaluateImportSetBlockersAsync` - the same guard, hints and DTO as LibraryStatus/imports. The engine's update set
(the declared floors) is **expanded with the dependency updates the import of those models pulls in**: `CkModelLibraryStatusService.ResolveImportSetAsync`
runs each declared model through `CkImportDependencyResolver` (the calculation of `ResolveDependencies` and the library status rows, no second one),
merges the results to one entry per model name at its highest version (a declared model is always part of the set, also when it is not published or
its branch is incompatible) and judges them as ONE set - so the preview/apply agree with the import dialog (`Industry.Energy-3.6.0` brings `Basic-2.5.0`:
blocked with `Basic-2.5.0` as `update`). `preview.ckUpdateSet` stays the engine's set (what the blueprint itself installs); the pulled-in updates show up
as the `update` of a blocker. No engine change. The import endpoints keep `EvaluateUpdateSetBlockersAsync` (their set is already complete).
The dependency set is calculated only in `CkImportDependencyResolver` (the code has no other catalog-dependency calculation), so there is nothing second to share. `System` / `System.*` are not judged. A host without the registration (or a platform-internal `ApplyBlueprintAsync(tenantId, id, force)` call, for example the
service-managed blueprints at tenant setup in `TenantContext`) runs unguarded.

| Endpoint | Behavior |
|----------|----------|
| `POST {tenantId}/v1/blueprints/updates/preview` | Read-only. Additive members `isBlocked`, `ckUpdateSet`, `blockers[]` (`CkUpgradeBlockerDto`). A guard failure does not fail the preview (the engine records it and an enforcing apply fails closed). |
| `POST {tenantId}/v1/blueprints/updates/apply` | `409 CK_UPGRADE_BLOCKED` (`application/problem+json`, `CkUpgradeBlockedError.CreateBlueprintResult`: `code`, `updateSet`, `blockers[]`, English `detail`, `blueprintId`) before the first change; installation record, history, CK models, runtime data unchanged. `dryRun: true` never refuses: 200 with `isBlocked` + `blockers`. |
| `POST {tenantId}/v1/blueprints/apply` | Same for a first install / re-apply (`forceBreakingUpdate` is separate from `force` = re-apply seed data). |

- **Override** = request member `forceBreakingUpdate` (default `false`). The blueprint endpoints are `TenantAssetApiReadWritePolicy`; a forced
  request additionally needs `DataModelManagementPolicy`, checked in the controller with `IAuthorizationService` (`403` otherwise, nothing
  called). A forced operation with blockers logs one WARN (tenant, caller via `UpgradeGuard/CallerDescription`, blueprint, update set,
  blockers) and returns the forced blockers in `blockers[]`. A guard failure is `500` even when forced.
- **GraphQL**: `blueprints { install }` and `blueprints { applyUpdate }` (role `AdminPanelManagement`) always enforce (no force
  input). The refusal is ONE error built by `GraphQL/CkUpgradeBlockedGraphQlError`: `extensions.code = CK_UPGRADE_BLOCKED`, `extensions.blueprintId`,
  `extensions.updateSet` and `extensions.blockers[]` (dependent, update, kind, subject, code, message, dependentIsInUpdateSet,
  suggestedDependentUpdate, resolveHint - the library-status blocker); `path` is set. The data goes into `ExecutionError.Extensions`
  explicitly: `ExecutionError.Data` is NOT sent to clients (GraphQL.NET `ExposeData` is off and must stay off - exception data can carry
  internals). The message is built here, not taken from the engine, and does not name the REST / CLI `ForceBreakingUpdate`. The mutation
  fields `install`, `applyUpdate` and `uninstall` are **nullable** (they were `NonNull`): a resolver error leaves the field `null` without
  the second "Cannot return null for a non-null type" error and without nulling `blueprints`. This is backward compatible for queries; a
  generated client type gains `| null`. `previewUpdate` / the update result expose `isBlocked`, `ckUpdateSet`, `blockers`
  (`BlueprintCkUpgradeBlockerDtoType`).
- Tests: `BlueprintsControllerUpgradeGuardTests`, `BlueprintCkUpgradeGuardTests` (unit), `CkModelLibraryStatusUpdateSetTests` (`BlueprintSet_*` / `BlueprintVerdict_*`: pulled-in dependency update blocks, safe set, no dependency updates, dedupe, blueprint verdict == `ResolveDependencies` verdict), `BlueprintCkUpgradeGuardSchemaTests` and `BlueprintsMutationCkUpgradeBlockedTests` (GraphQL: runs `BlueprintsMutation` and the error serialization with a faked `IBlueprintService`),
  `Tenant/BlueprintCkUpgradeGuardFlowTests` (integration: real controller, blueprint service and MongoDB; the guard decision is switched by
  `SwitchableBlueprintCkUpgradeGuard`; blueprint `CkGuardFlowBp`). User documentation: octo-documentation `constructionKits/blueprints#ck-upgrade-guard`.
