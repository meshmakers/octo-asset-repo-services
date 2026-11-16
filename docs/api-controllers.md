---
description: REST controllers of the system and tenant APIs: tenants, features, models, blueprints, files, diagnostics, tenant delete and scratch tenants.
applies_to: **/SystemApi/**, **/TenantApi/**, **/Consumers/**, **/Services/DefaultConfigurationCreatorService.cs
---

# API controllers and tenant management

## 3. API Controllers
Located in versioned API folders:

**System APIs** (`SystemApi/v1/Controllers/`):
- `DiagnosticsController.cs` - Health and diagnostics
- `BlueprintsController.cs` - Blueprint management
- `CkModelCatalogController.cs` - CK model catalog browsing, search, and cache refresh
- `TenantRegistryController.cs` - `POST system/v1/tenants/reconcile-registry?apply=false`: repairs the system tenant registry (AB#6971, AB#6941). Tenants that a parent lists in its own registry (visible in `GetTenants -r`) but that lack their system registry row (missing in `GetTenants -a`, so client mirror provisioning, token cleanup, CORS and health checks skip them) get the row inserted through the engine's `ISystemContext.ReconcileTenantRegistryAsync`. **Dry run is the default**; rows are only inserted, never changed or deleted; disagreeing rows and unreadable parent registries are returned as `conflicts`; idempotent. Runs in one admin transaction. Caller check `SystemAdministratorGuard` (`Security/`, shared with the orphan sweep and the `methodHandlers` query: system-tenant token, user tokens need `AdminPanelManagement`, else 403) plus `SystemAssetApiReadWritePolicy`. Report: `applied`, `walked`, `inserted[]`, `alreadyPresent`, `conflicts[]` (with reason), `summary`. Needs an engine release that contains `ReconcileTenantRegistryAsync` (octo-construction-kit-engine-mongodb PR 172). Runbook: dry run, review inserts and conflicts, then `apply=true`, then re-run the client mirror backfill.
- `FilesOrphanSweepController.cs` - `POST system/v1/files/orphan-sweep/{tenantId}?remove=false&gracePeriodMinutes=1440`: finds (dry run, the default) and removes (`remove=true`) orphaned GridFS bytes of linked binaries with the engine's `GridFsOrphanSweeper`; caller check `SystemAdministratorGuard` (shared with `reconcile-registry`: system-tenant token, user tokens need `AdminPanelManagement`, otherwise 403) plus `SystemAssetApiReadWritePolicy`; grace period default 24 h, minimum 10 min (400 below); returns a count report, logs counts only (AB#6541, see `docs/gridfs-orphan-sweep.md`)

**Tenant APIs** (`TenantApi/v1/Controllers/`):
- `TenantsController.cs` - Tenant management. `GET {tenantId}/v1/tenants` returns **only the child tenants** of the current tenant; `GET {tenantId}/v1/tenants/self` returns the current (own) tenant, including its `Database`. Keeping the two apart is deliberate (AB#4601): AB#4432 had injected the own tenant into the *list* as a virtual index 0, which made every list-row action (`Detach`, `Delete` in the Refinery Studio context menu) offer itself on the tenant the operator was signed into — an operation the API must never expose. The own tenant is only resolvable server-side (its `Database` comes from the request's `ITenantContext`; the registry entry describing a tenant lives in its **parent's** database and in the system database, never in its own), which is why `self` exists at all instead of the frontend deriving it. `self` needs no extra tenant check beyond its `TenantAssetApiReadOnlyPolicy`: it sits under the `{tenantId:tenantId}` prefix, so `TenantAuthorizationMiddleware` already 403s a user token whose `tenant_id` claim does not match the route (client-credentials tokens are exempt there by design). Child tenants come back in the underlying query's default order — the endpoint imposes **no explicit sort**, so cross-page ordering is only as stable as that default (`GetChildTenantsAsync` in the engine exposes no sort parameter). **Roles (AB#4497).** The reads are scope-only. The administrative actions `Post` (create), `Attach`, `Detach`, `Clear`, `Delete`, `ReRunSetup`, `ClearCache` and `RecomputeDisplayNames` carry `TenantAdministrationPolicy` (`AssetRepositoryAuthorizationPolicies`): full-access scope **plus the tenant role `TenantManagement`** in the caller's token (one `role` claim per role; both the raw `role` and the inbound-mapped `ClaimTypes.Role` spelling are probed); a caller without the role gets 403. The Studio's route guard stays as UX only. octo-cli `CreateTenant`, `ClearCache` and `RecomputeDisplayNames` (and the MCP `clear_tenant_cache`) therefore need `TenantManagement`; the following `ProvisionCurrentUser` is the identity service's admin-provisioning endpoint, which accepts `TenantManagement` or `UserManagement` (AB#5859). Tests: `TenantAdministrationAuthorizationTests` (per action: no role = refused, role = allowed, other roles refused, production policy registration). A new tenant-administration action must carry `TenantAdministrationPolicy` and a negative test. The policy is defined centrally (`AddTenantRolePolicy` and `AddOctoTenantRoleAuthorization()` of octo-common-services, AB#7319) and also guards the Stream Data `enable`/`disable` toggles (`StreamDataToggleAuthorizationTests`); the 403 carries the reason in the AB#5227 shape.

  **Default database name and the tenant-database prefix (AB#8797).** `Post` takes `databaseName` as optional: without it
  the controller derives `TenantDatabaseNames.DeriveDefault(System:TenantDatabasePrefix, childTenantId)` (engine-mongodb),
  i.e. the tenant id, prefixed when the instance has a prefix. An explicit name is passed on unchanged; with a prefix
  configured the engine's namespace gate refuses a create or attach whose name lacks it with the generic
  `Database name '<x>' is not available.` (409, reason only in the log). The prefix comes from the bound `System` section
  (`IOptions<OctoSystemConfiguration>`), no own setting here. Tests: `TenantsControllerTests.Post_WithoutADatabaseName_*`,
  `Post_WithADatabaseName_PassesItOnUnchanged_EvenWithAPrefix`, `Post_WhenTheEngineRefusesTheName_AnswersTheGenericConflict`.

  **Ownership is settled before the platform-global stores are read (AB#4763).** The tenant lifecycle
  store knows *every* tenant on the platform, so any endpoint that answers based on its contents leaks
  the state of tenants outside the caller's subtree. `GetLifecycle` and `Delete` therefore both check
  `IsChildTenantExistingAsync` **first** and 404 otherwise — the same idiom `Get(id)` already used —
  and only then consult the store. `GetLifecycle` without that gate defeated the whole point of the
  engine's reason-free conflicts: a caller took the deliberately vague *"Tenant ID is already in use"*
  from `Post` and then read the colliding tenant's database name, last error and lease owner from here.

  **A stale child entry is removed, never deleted through (AB#6759).** Right after the ownership probe,
  `Delete` calls the engine's `RemoveStaleChildTenantEntryAsync`: when the caller's registry still lists a
  tenant that is not registered as its child any more (deleted from the system tenant before the engine
  cleaned the parent row, or its id now used below another parent or on another database), only that row
  goes and the answer is 200 — no lifecycle tombstone, no events, no database drop, no scratch-marker or
  setup-retry cleanup, no capability read, because all of those are keyed by the tenant id or database name
  and would hit the tenant that uses them today. Before the fix this path answered 500 or, with a reused
  id/database, dropped the other tenant's database. Needs the engine release with
  `ITenantContext.RemoveStaleChildTenantEntryAsync`. Detection of stale rows per cluster (read-only):
  `octo-cli -c ReconcileTenantRegistry` dry run, conflicts whose reason starts with `stale entry`.
  Tests: `TenantsControllerTests.Delete_OfAStaleEntry_*`.

  **Conflicts are the engine's two generic messages, verbatim.** `Post`'s in-flight-deletion guard
  reuses `TenantException.TenantIdNotAvailable(...).Message` rather than naming the lifecycle state,
  because it queries the store globally. `Delete`'s "still being created" guard may state its reason —
  ownership is already established at that point — and must, since *"already in use"* is nonsense as
  the answer to a delete. `Attach` maps `TenantException.IsConflict` to **409** and the namespace
  gate's format-validation `ArgumentException` to **400**, exactly like `Post`; it used to return
  400 for the identical conflict because `TenantException` derives from `PersistenceException`, and
  500 for the identical invalid input because nothing caught the `ArgumentException`.

  **`Delete` ends with a settle tombstone, not a clean slate (AB#4829).** Events and setups already
  in flight across the platform (queued `PosUpdateTenant` echoes, setups whose tenant resolve predates
  the registry commit) can re-seed `tenant_setup_retry` rows and lazily re-create the dropped database
  as an empty `CkModel`+`SysLock` shell — and since AB#4762 the create path never reclaims such a
  leftover, so it would permanently block its own database name. The delete therefore clears the retry
  rows, then **upserts** the `Deleting` tombstone via `EnsureDeletingAsync` (covering legacy tenants
  without a lifecycle record, recording the database name, restamping the settle clock at the drop)
  and returns with the tombstone in place. The Standardized reconciler's **settle sweep**
  (`ReconcileDeletingTenantsAsync` in octo-common-services; active only where the lifecycle store is
  wired, i.e. here) completes the delete ~90–120 s later: it re-drops a resurrected shell (never a
  database another tenant has legitimately claimed), clears re-seeded retry rows, and removes the
  tombstone. Until then a re-create of the tenant id answers a retryable 409 via the `Deleting` guard.
  A **failed** delete also leaves its tombstone — the sweep arbitrates: tenant still fully registered
  → tombstone rollback (the delete died before its metadata commit); registry entry gone → the delete
  is completed, including the drop. Crashed deletes therefore converge in both directions instead of
  leaving operator-only orphans. During the settle window the tombstone is load-bearing, so every
  route that could corrode or bypass it is guarded: `Post` **and** `Attach` answer the generic 409
  while it stands, `rerunSetup` refuses it (`RequeueForReconcileAsync` returns null for Deleting), no
  lifecycle writer other than the delete/sweep can leave the Deleting state, and the engine's restore
  refuses a target tenant id or database name that a Deleting tombstone still holds (AB#4829).

  **`Delete` and `Detach` refuse while a capability is still enabled (AB#4255).** Stream Data,
  Communication, Reporting and AI Services own state outside the tenant database — CrateDB archives,
  adapters and pools, report storage, AI configuration — that a plain metadata delete/detach would
  orphan in the cluster. Both endpoints therefore answer **409** while any of the four enabled flags is
  set, naming exactly the enabled capabilities, their octo-cli disable verbs (which act on the tenant of
  the active context — there is no tenant argument), the Studio alternative where one exists (not for
  AI), and the advice to `Dump` first if the data is still needed. The flags are the
  `RtTenantConfiguration` documents in the **child's own database**, read through
  `ITenantCapabilityStateReader` from octo-common-services; a missing key and a kept `false` flag both
  mean disabled (Communication/Reporting/AI delete the key on Disable, Stream Data keeps it). The guard
  sits at a fixed place in the order of checks: **after** the ownership probe (the read resolves the
  child and opens its database, which for a foreign tenant would be the existence oracle AB#4763 closed),
  on `Delete` **after** the `Creating` guard (resolving a half-built tenant runs the CK auto-imports on
  it) and **before** `MarkDeletingAsync` (a refused delete must not tombstone the tenant id for the
  settle window). A read failure answers 500 and writes nothing — an unreadable state is never
  "disabled" — and there is deliberately no force flag. The guard lives in the controller only, so
  `Dump` (bot-services, `ISystemContext.BackupTenantAsync`) is untouched and works regardless of
  capability state. `Detach` gained the pieces it was missing for this: the ownership probe (foreign
  ids answer the reason-free 404 instead of the engine's 400 naming the tenant), a declared 409, and
  the `TenantException.IsConflict → 409` / `IsTenantNotFound → 404` split — its `PersistenceException`
  branch used to swallow both into 400, the defect `Attach` had under AB#4763.

  Still open on this controller (filed separately): `ReRunSetup` and `ClearCache` accept any tenant id
  with no subtree check — `ClearCache` still publishes update events for nonexistent tenants. The
  damage is now self-limiting (the update-event consumer drops unregistered tenants and the drain loop
  clears not-found entries terminally, AB#4829), but the missing subtree check remains. `Clear` has no
  ownership probe, lifecycle guard or capability guard at all, and `Detach` (unlike `Delete`) still has
  no `Creating`/`Deleting` lifecycle guard — both are outside AB#4255 step 1.

  **Stream Data (AB#4255 step 2, last part).** `StreamDataController.Disable` maps the engine's
  `StreamDataDisableBlockedException` (thrown by `TenantContext.DisableStreamDataAsync` while any archive
  of the tenant is still `Activated`) to **409** with an `OperationFailedErrorDto`: the engine names the
  archives, the controller appends the remediation (`DisableArchive` / `DeleteArchive`, Studio
  **Repository > Archives**, retry `DisableStreamData`); other `StreamDataException`s and
  `ConfigurationException`s stay 400, anything else propagates. `Delete` passes `dropStreamData: true`
  to the engine's `DeleteChildTenantMetadataAsync`, so the CrateDB tables of the tenant's own archives
  (collected before the record is deleted; never "everything in the schema" — tenants whose ids differ
  only in `-`/`_` share a CrateDB schema) go with the database — best-effort, gated on
  `StreamData:Enabled`. Engine `Clear` does the same; a restore over an existing tenant, the
  create-rollback and the settle sweep only swap/re-drop the database and keep the tables; `Detach`
  keeps everything. `Clear` therefore drops the tables of Activated archives without refusing (no guard
  of its own, see above). With CrateDB unreachable the drop of a tenant with archives blocks for up to
  ~2 min in the CrateDB resilience pipeline before logging the error — the delete still succeeds.
- `FeaturesController.cs` - `GET {tenantId}/v1/features/status` (AB#4884): aggregate enabled-state of the four capabilities the delete/detach guard evaluates, read through the same `ITenantCapabilityStateReader` — one state source for the Studio's Tenant Features panel and the guard, so they never disagree. Stream Data additionally carries the instance-level `StreamData:Enabled` flag. Whether Reporting/AI are installed at all is NOT answered here — that comes from the `_configuration` discovery document (empty URL = not installed). Read failures propagate as 500 (an unreadable state must never render as "disabled"). Replaces the former `GET streamdata/status`.
- `ModelsController.cs` - Construction kit and runtime model import/export (includes `ImportFromCatalog` endpoint)
- `LargeBinariesController.cs` - Binary file download. Falls back to magic-byte sniffing via `BinaryContentTypeDetector` when the stored `ContentType` is missing or `application/octet-stream` (legacy data uploaded before detection existed). For non-seekable source streams the head bytes are re-prepended via `PrependedReadStream`.
- `FilesController.cs` - **Platform file system bytes API (AB#6171 / AB#6174, zip AB#6225)**, `{tenantId}/v1/files`. See "Platform file system" below.
- `DiagnosticsController.cs` - Per-tenant diagnostics.
  - `GET slow-mongo-queries` returns the recent in-memory `SlowQueriesBuffer` entries filtered by `Database == tenantId` (AB#4212); backs the Refinery Studio Diagnostics → Slow Queries page.
  - `GET index-usage` (AB#4224 / Stage 3) runs MongoDB's `$indexStats` across every non-system collection in the tenant's database, classifies each index as `builtin` / `unused` / `lowUsage` / `used`, and orders Unused first then LowUsage. Query params: `minAgeDays` (default 7), `lowUsageOps` (default 10), `includeUsed` (default false — Builtin/Used are filtered out unless explicitly requested). Delegates to `IIndexUsageService` from the engine; tenant resolution happens inside the service via `ISystemContext`. Backs the Refinery Studio Diagnostics → Index Usage page.

## Scratch tenants (AB#7192)

`POST {tenantId}/v1/tenants?childTenantId=..&databaseName=..&scratch=true` creates a child as a **scratch tenant**: the creating
client-credentials client of the direct parent becomes administrator (`TenantOwners`) of it, so an agent can work in the tenant and clean
it up without a human login (identity side: octo-identity-services `CLAUDE.md`, "Scratch tenants").

- Accepted only for a token that is a client-credentials token (no `sub`) with a `client_id`, issued for **the route tenant itself** (the direct
  parent), and the role `TenantManagement` (the endpoint's `TenantAdministrationPolicy`). Anything else: `403` with the reason, nothing written.
  The decision is `ScratchTenantMarker.TryCreateForCaller` (octo-sdk).
- The `ScratchTenantMarker` (creator client, parent, time) is written to the system tenant configuration **before** `CreateChildTenantAsync`
  (identity sets the tenant up on `PosCreateTenant` and must find it then) and taken back when the create fails by a **compare-and-delete**
  (`DeleteIfMatchesAsync`: only the exact marker this request wrote - creator client and creation time - so a parallel request for the same tenant id
  keeps its own; a create of a taken id answers the generic conflict and leaves the existing marker alone). A create without the flag clears a
  stale marker of an id that is gone; `Attach` does the same; `Delete` and `Detach` remove the marker with the tenant. `IScratchTenantMarkerStore`.
- `GET tenants`, `GET tenants/{id}` and `GET tenants/descendants` carry `isScratch`, `scratchCreatorClientId`, `scratchCreatedAtUtc` and
  `scratchAccessRevoked` on the tenant DTO (not `tenants/all`: it would read one marker per tenant of the installation).
- Tests: `TenantsControllerTests` (user token, token of another tenant, no tenant / no client, creator recorded before the create, taken id, failed
  create, plain create, delete / detach / attach, listing and details).
