---
description: System.Files model, file and folder REST and GraphQL API, name uniqueness, permission baseline, large binaries and the file system guards.
applies_to: **/Files/**, **/Controllers/File*.cs, **/Controllers/LargeBinaries*.cs, **/FilesAccessMetrics.cs
---

# Platform file system

## Platform file system

Files and folders are a platform capability, independent of Reporting. The **System.Files 1.0.0** CK model
(source in octo-construction-kit-engine `src/SystemFilesCkModel`, package
`Meshmakers.Octo.ConstructionKit.Models.System.Files` from the libs train, depends on `System-[2.5,3.0)`) is
**installed by octo-platform-services** (service-managed, like System.UI), together with the default root
`Files`. This service references the package for the generated types only and neither imports the model
nor creates the root. Model content:
`FolderRoot`, `Folder`, `FileSystemItem` (attribute `Content`, BinaryLinked), abstract `FileSystemEntity` /
`FileSystemContainer`, all in `RtEntity_SystemFilesFileSystemEntity`; the tree is `System/ParentChild`
(origin = child, target = parent).

- **Tenant setup.** `DefaultConfigurationCreatorService` has **no** `ImportCkModelAsync` override and
  `StartTenantAsync` creates **no** default root: platform-services installs System.Files (engine descriptor, on
  tenant resolve, downgrade guard) and ensures the root `Files` in its tenant setup (create, attach, restore,
  full update, cold start). The order of the two services at a tenant start does not matter: this service needs
  nothing of System.Files during its setup. `GetCkModelIds` (`System.Files-[1.0,2.0)`) stays so that later minors
  migrate through the standard upgrade path; a tenant without System.Files is skipped there ("not installed").
- **While the model is missing** (platform-services not yet through, or not deployed): the file API answers
  `GET capabilities` with `available: false`, `reason: SYSTEM_FILES_MISSING`. Nothing fails, nothing
  waits for an asset-repo restart; once platform-services has installed the model and created the root, the file API
  is available on the next request. An installation without platform-services (`platformServices.deploy: false`)
  never gets System.Files and stays at `SYSTEM_FILES_MISSING`.
- **Sibling-name uniqueness under concurrency.** Names are unique per parent (case-insensitive) but the
  platform has no unique index for them (the parent is an association, not a field of the entity), so uniqueness is
  guaranteed by `FileSystemNameLocks` (singleton via `FileSystemService.NameLocks`): an in-process async lock per
  `parentRtId + upper(name)`, owned by the `IOctoSession` and held **until the transaction has ended**. A second
  writer waits, then re-reads the committed state over an unfiltered session: GraphQL create/rename/move get
  `NAME_CONFLICT`, `PUT ... createFolders=true` re-resolves the folder the first request created (the REST upload
  locks the first *missing* folder of the path before it starts its transaction, so its snapshot sees the
  predecessor's folder; deeper folders are created below a folder nobody else can see yet). Release points:
  `OctoSessionListener.AfterExecutionAsync` (GraphQL) and the `finally` of `FilesController.UploadByPath` /
  `UploadIntoFolderById`. Wait limit 30 s (in-process wait and lease wait together), then a retryable 409.
  **Across replicas:** after the in-process lock the key is also leased in MongoDB
  (`IFileSystemNameLeaseStore`, collection `FileSystemNameLease` of the tenant database, `_id` = the lock key,
  `owner`, `expiresAt`): an atomic `findOneAndUpdate` upsert that matches only an expired lease, polled with a short
  jittered back-off; released with the session (`FileSystemNameLocks.ReleaseAsync`, after commit/abort). It uses the
  raw admin client of `ITenantMongoDatabaseProvider` (`Services/Files/TenantMongoDatabaseProvider.cs`; a connection outside the request transaction, so other replicas see the lease
  at once). Lease duration 2 min (longer than MongoDB's 60 s transaction limit; **not renewed**), a TTL index
  (10 min) sweeps leases a crashed replica left behind, an expired lease is taken over at once. Without MongoDB admin
  credentials the store reports "unavailable" (one warning) and only the in-process lock applies. **Why a lease and
  not a unique index:** the sibling relation is an association document in another collection than the entity's
  `name`, so no MongoDB unique index can span it; the engine has no unique-key concept for it either. **Trade-off:**
  two extra MongoDB round-trips per name-creating request and a 2 min worst case after a replica dies while holding
  a lease; a writer that bypasses the asset repository (raw database access) is not covered. Existing tenants with duplicate sibling names are not touched: nothing fails at start, the paths
  stay `AMBIGUOUS_PATH` and every such answer logs a warning (`Duplicate sibling names under ...`).
- **Metadata = generic GraphQL** (decision Q1). `FileSystemMutationGuard` is called by the generic
  create/update/delete resolvers (typed and `runtimeEntities`) whenever a System.Files entity is written or a
  `System/ParentChild` association touches one: root creation needs the role `FileManagement`, root
  well-known names are unique and not one of `capabilities`, `zip`, `items`, `stats` (route segments of the
  bytes API) nor `ReportingAssets_*` (system only); names are unique per parent (case-insensitive) on
  create/rename/move; every entry has at most one parent (a move must DELETE the current parent with the
  CREATE), roots never get one; `Files`, `ReportingAssets_*` and blueprint roots cannot be renamed/deleted; no
  folder moves below itself; the target folder must be visible to the caller. **Deleting a root/folder deletes
  the whole subtree** (at most `MaxDeleteEntries`, default 2,000 until the index on `fs.files.metadata.rtEntityId` has landed and been measured, then raised by measurement — 5,000 took 36.4 s of the 60 s transaction limit in the local E2E —, per mutation — GridFS deletes are not part of
  the transaction, so a cascade must finish well inside the transaction lifetime), System.Files entries are
  always erased (GridFS bytes included), and the cascade runs in the caller's session, so an entry the caller
  may not delete fails the whole mutation (FORBIDDEN, no rtIds named). Runtime-query row mutations
  (`runtimeQuery { create/update/delete }`) refuse System.Files entities and their tree. Conflicts are checked
  over an unfiltered session. Error codes: FORBIDDEN, NAME_CONFLICT, RESERVED_NAME, PROTECTED_ROOT,
  MOVE_INTO_ITSELF, INVALID_NAME, INVALID_REQUEST, LIMIT_EXCEEDED.
- **Bytes = REST** `FilesController`: `PUT|GET {root}/{**path}` (upload `?conflict=fail|replace|keepBoth`
  `&createFolders`, download `?inline`), `POST items/{rtId}/content?name=` (upload into folder),
  `PUT|GET items/{rtId}/content`, `GET stats?root=&path=` / `GET items/{rtId}/stats` (deep counts incl.
  `hiddenEntries` hidden by data permissions), `POST zip` (`{ items: [{root, path}], rtIds, fileName }`,
  streamed, limits checked before the first byte), `POST linked-counts` (`{ rtIds }` → `{ counts: {rtId: n} }`,
  every association except `System/ParentChild`, any role/direction, visible entries only), `GET capabilities`
  (tenant-aware: `available: false`, `reason: SYSTEM_FILES_MISSING` without System.Files; plus `maxDeleteEntries`).
  GraphQL writes on files and folders answer a data-permission denial with the code `FORBIDDEN`. Reads run in the caller's session
  (hidden = 404), writes through the engine's data-permission write guard (403 FORBIDDEN, message 4973).
  The write guard asks for visibility first: update/replace/delete of a file the caller cannot see
  (no Read grant, or owned-only Read and not the owner) answers like a missing file (404 / not found), and 403
  FORBIDDEN is only given for a file the caller can see but may not write. A role with Write/Delete but no Read
  (files.manage only) therefore cannot update/delete existing files, but still creates files and folders
  (inserts are not subject to the visibility rule, nor is the parent link of the inserted entity).
  The same rule holds for the generic GraphQL `runtimeEntities.update` / `delete` of any CK type: a record the caller
  may write but not see answers with the not-found error `ASSET1001`, identical to a non-existent rtId, and stays
  untouched. Pinned by `GraphQL/HiddenRecords/HiddenRecordMutationGraphQlTests` (fixture `HiddenRecordGraphQlTestFixture`:
  P1 Write+Delete on all records, P2 Read on own records, variant with P1 only).
  Problem details carry a stable `code` extension. Uploads are buffered to a temp file (the engine needs a
  seekable stream) while `MaxUploadBytes` is enforced; Kestrel's body limit is raised per request.
- **Role model.** The API scope only opens the door; the file roles decide. Built-in baseline
  (`FileSystemPermissionBaseline`, wired by `AddFileSystemPermissionBaseline` as a wrapper around the engine's
  `IDataPermissionResolver`, only for tenants that have System.Files): **read** (list, download, stats, zip,
  linked counts) needs `FileViewer` or `FileManagement`; **write and delete** (upload, replace, create/rename/move/
  delete, root creation) needs `FileManagement`. Without a file role REST answers **403 FORBIDDEN** (checked up
  front by `FileSystemAccess`, before any body is buffered or any entry looked up), GraphQL mutations fail with
  `FORBIDDEN`, GraphQL reads return nothing (engine read filter). The baseline steps aside as soon as the tenant
  has its own enforcing data policy targeting `System.Files/FileSystemEntity` (the `System.Identity.Bootstrap` seeds `files.read` /
  `files.manage`, or an administrator's custom roles): then only the tenant policies decide, so a custom role
  can be granted write access without `FileManagement`. System contexts (CK migrations, blueprint apply) are
  never restricted. The baseline protects the **REST/GraphQL surface of asset-repo only**: the in-process pipeline engine (mesh adapter file nodes) is governed by the identity seeds (Bootstrap 1.6.0 policies), not by the baseline, so until a tenant has been re-seeded with 1.6.0 its pipelines are more permissive on System.Files than REST. The baseline steps aside only for an **enforcing** tenant rule on `System.Files/FileSystemEntity` (any actions: the engine denies the actions no rule grants); an AuditOnly rule or a rule on leaf types only keeps it active. **Interim tenant guard.** While `UserTokenEnforcement` is LogOnly, `FileSystemAccess.EnsureTenantMatch` (shared core `FilesTenantGuard`) refuses on every Files route (incl. `capabilities`) a bearer **user** token (has `sub`) whose `tenant_id` differs from the route tenant: 403 FORBIDDEN, counter `octo.files.access_denied` with `action=tenant_mismatch`. It mirrors the `TenantAuthorizationMiddleware` user-token decision: client-credentials tokens (no `sub`), cookie principals and requests without a bearer header are not touched; no parent-tenant exemption (the middleware's parent rule applies only to endpoints marked `IAllowParentTenantAdministration`, and on the GraphQL endpoint only to requests made exclusively of the closed parent-administration method calls and operation reads (AB#8986); it gives a parent no data routes of its child), so a parent-tenant token is refused exactly as under the planned platform Enforce. **The same guard covers GraphQL and largeBinaries for System.Files**, with the same decision (`FilesTenantGuard`: bearer user token with `sub`, `tenant_id` != route tenant, case-insensitive, no parent exemption; service tokens and cookie principals untouched), the same counter (`action=tenant_mismatch`, no tenant/account label; the log line names account, client id and both tenants, never a token) and the same switch. GraphQL: `FilesTenantGuardListener` (document listener, registered before `OctoSessionListener`, so a refused request opens no session) refuses the **whole request** with the error code `FORBIDDEN` (HTTP 200 with errors, like other GraphQL errors) when the token tenant differs **and** the operation touches System.Files (`FilesGraphQlInspector`: a selected field with a System.Files `CkId` metadata or return type `SystemFiles*`, an inline/named fragment on such a type, or any argument/variable value naming `System.Files` - this covers `runtimeEntities(ckId:)`, `ckTypeId` in create/update inputs and delete by `RtEntityId`). Every other operation of the same token (cross-tenant topology reads of meshmakers-app, octo-cli, introspection) is untouched, and a token of the route tenant pays nothing (the mismatch test runs first). Not covered statically: a persisted query addressed by rtId (`runtimeQuery`) whose target type is System.Files, and generic association hops from non-Files entities to Files entities (other types are protected only by the tenant's own policies until platform Enforce). `LargeBinariesController.Get` answers **403** when the binary is owned by a System.Files entity (ownerless and other binaries unchanged). All three go away when Enforce is armed platform-wide. Switchable without a release: `OCTO_Files__TenantGuard=LogOnly|Enforce` (default Enforce; an invalid value also means Enforce, with a warning; LogOnly logs `tenant_mismatch` with account, client id, token tenant and route tenant and lets the request pass). Remove it when Enforce is armed platform-wide. `GET capabilities` stays open to every caller with the API scope **on purpose**: it only advertises limits and whether System.Files exists (Studio calls it before it knows the caller's roles to decide whether to show the Files page); it names no entry. GraphQL reads of a caller without a file role are filtered by the engine and return **0 rows, not an error** (a folder lookup returns no item); only REST and the mutations answer 403 FORBIDDEN. `GET stats` without `root` answers 400 INVALID_REQUEST. Clients with a machine
  token that carry no file role — customer client-credentials clients and octo-cli `LogInClientCredentials` — get 403 on
  every Files endpoint until the client is given `FileViewer`/`FileManagement` (`octo-cli AddClientToRole`).
  The pipeline service accounts (`octo-pipeline-sa-*`) are granted `FileManagement` by identity; their file nodes
  and the Reporting service use in-process sessions (Reporting: no security context = system) and do not go through
  this REST API.
- **Limits** (`FilesOptions`, section `Files`, env `OCTO_Files__…`): MaxUploadBytes 100 MB, ZipMaxFiles 1,000,
  ZipMaxBytes 500 MB, PreviewMaxBytes 20 MB (advisory). The ingress must accept more than MaxUploadBytes
  (octo-helm-core `ingress.proxyBodySize`, 110m). GraphQL multipart uploads get the same limit (+1 MB, set
  per request for `multipart/*` only; JSON GraphQL bodies keep Kestrel's 30 MB).
- **Download headers** (`FileResponseHeaders`, also on `/v1/largeBinaries`): `Content-Disposition` with ASCII
  fallback + `filename*=UTF-8''…`, `Access-Control-Expose-Headers` (the shared CORS policy does not expose
  them), `ETag` = binary id, nosniff. Inline delivery is an allow-list (images except SVG, audio, video, PDF,
  text/plain, CSV, JSON); everything else is an attachment with a `sandbox` CSP. largeBinaries default to
  inline for passive content (`?inline=false` = attachment). Zip entry names are sanitized (no separators,
  no `..`); a file whose GridFS bytes are missing is skipped in a zip and answers 404 on download.
- Known engine limitations: a nested association connection with `first: 0` fails ($slice) — ask `first: 1`;
  with `fieldFilter` + `sortOrder` + `first` its totalCount is the unfiltered association count, which answers
  INCOMPLETE_SLICE (or a wrong hasNextPage) — name lookups and searches must not sort (skipped repro test).
- Zip writes need synchronous IO for ZipArchive's small header records (Kestrel forbids it by default);
  the zip action allows it for its own response only.
- **Denials are observable.** Every Files API 403 from the up-front gate (`FileSystemAccess.EnsureAsync`: REST
  read/write and the GraphQL mutation guard incl. delete) and from the controller's engine data-permission denial increments the
  counter `octo.files.access_denied` (meter `Meshmakers.Octo.AssetRepository`, attributes `tenant_id`, `account` = subject id /
  client id, `action` = read|write|delete) and logs a warning with the same fields. Fault-only, so the `account` label stays small;
  no tokens or secrets. The Dash0 rule (meshmakers-infrastructure) fires on this counter; fix = grant FileManagement / FileViewer.
  Not yet covered: GraphQL mutations denied by the engine itself (`TranslateForbiddenAsync`), which have no tenant/account at hand.
- Tests: `tests/AssetRepositoryServices.IntegrationTests/Files/*` (`FilesTestFixture` plays platform-services: it imports System.Files and creates the
  default root, and has a switchable data-policy table). `FilesRawMongoFixture` (collection `FilesRawMongo`, own database) adds raw
  MongoDB access for the name lease and GridFS orphan sweep tests.
