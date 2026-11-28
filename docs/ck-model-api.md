---
description: Blueprint-locked entity errors, released-document errors, ValidateCk, export entry points, the removed files migration, and the CK model and blueprint catalog REST APIs.
applies_to: **/BlueprintLock/**, **/ReleasedDocuments/**, **/CkModelValidation/**, **/CkModelCatalog/**, **/CkModelCatalogController.cs, **/BlueprintsController.cs, **/ModelsController.cs
---

# CK model and blueprint APIs

## Blueprint-locked entities: stable error contract

The engine write guard (`DataPermissionWriteGuard`; opt-in by `DataPolicy.ProtectBlueprintLocked`) refuses a
change set that updates/deletes an entity with `RtBlueprintLocked = true` (or sets/changes the three blueprint
attributes) of an opted-in type with an error `OperationMessage` of number **6384**
(`RtBlueprintLockProtectionNames.ForbiddenMessageNumber`), location `{ckTypeId}@{rtId}`. This service maps it (`BlueprintLock/`):

- `BlueprintLockError` extracts the offenders (`ckTypeId`, `rtId`, reason `EntityLocked` | `ProtectedAttributes`) from the
  messages. The reason is derived from the engine message text ("managed by blueprints" = `ProtectedAttributes`); the
  integration tests run against the real engine message and fail if the text drifts.
- `ResolveConnectionContextExtensions.ValidateOperationResult` throws `BlueprintLockedException` (an
  `AssetRepositoryException`), `HandleException` maps it (and a `RuntimeRepositoryException` carrying 6384) to the GraphQL
  error **`BLUEPRINT_LOCKED`** (`Statics.GraphQlBlueprintLocked`) with `extensions.messageNumber`, `ckTypeId`, `rtId`,
  `reason` and `items` (every refused entity). The whole mutation is rejected, so a batch with one locked entity writes nothing.
- REST: `BlueprintLockedExceptionFilter` (registered for all MVC controllers) turns the same refusal (a
  `RuntimeRepositoryException` / `BlueprintLockedException` that reaches the filter) into `403` `application/problem+json`
  with `code = BLUEPRINT_LOCKED`. There is no REST endpoint that writes runtime entities directly; the filter is the
  contract for any action that lets the refusal escape. `Models/ImportRt` starts a job and catches everything itself, so
  an import rejection shows up in the job result, not as a 403.
- Callers: only `IsSystem` sessions bypass the guard. A pipeline node with `Identity: ServiceAccount` runs as a user
  (guarded); `Identity: System` and the blueprint services are exempt.
- Tests: `tests/.../GraphQL/BlueprintLock/BlueprintLockErrorGraphQlTests.cs` (real engine guard behind the real mutations;
  the policy table is supplied through the `IDataPermissionResolver` seam by `BlueprintLockGraphQlTestFixture` because the
  System.Identity CK model is not part of the test tenant) and `tests/.../UnitTests/BlueprintLock/`.
- User documentation: octo-documentation, `dataAccess/runtime/errorHandling` (#blueprint-locked-entities).

## Released documents: stable error contract

The engine locks a released document (a document whose state attribute holds one of the `ReleasedValues` of its number
range) against every write and every caller, system sessions included; it also enforces the storno rules and the
append-only audit trail (engine design note `docs/concept-number-ranges-and-released-documents.md`). This service
implements no lock of its own: it maps the engine's exceptions to the error contract (`ReleasedDocuments/`).

| Engine exception | Code (`extensions.code`) | Message numbers | Extensions |
|---|---|---|---|
| `ReleasedDocumentLockedException` | `RELEASED_DOCUMENT_LOCKED` | 168 | `messageNumber`, the first document's `rtId`, `ckTypeId`, `operation` (`update`, `replace`, `insert`, `delete`, `association`) and `numberRange`; `items` with `rtId`, `ckTypeId`, `operation`, `changes` (attribute names, never values), `state` and `numberRange` per refused document |
| `StornoRefusedException` | `STORNO_REFUSED` | 169 to 172 | `messageNumber` of the first refusal; `items` with `messageNumber`, `location` (`{ckTypeId}@{rtId}`) and `message` |
| `DocumentAuditEntryAppendOnlyException` | `DOCUMENT_AUDIT_ENTRY_APPEND_ONLY` | 173 | as `STORNO_REFUSED` |

- GraphQL: `HandleException` maps them (also wrapped) before the generic `PersistenceException` branch (`ASSET1001`). The
  whole mutation is rejected; a batch with one released document writes nothing. Clients read the code, never the text.
- REST: no REST endpoint writes runtime entities synchronously, so there is no REST mapping. REST writes go through the
  asynchronous `Models/ImportRt` job (bot service): a write to a released document fails the job with
  `RELEASED_DOCUMENT_LOCKED` / message 168. The job's `errorMessage` is the engine message, which starts with
  `RELEASED_DOCUMENT_LOCKED: ` and names every refused document with message 168. An import of a document the tenant
  does not hold yet is allowed and keeps the number of the file (design note D10).
- Pipelines (mesh adapter, `ApplyChanges@1` and `@2`) fail the execution with the same code.
- Tests: `tests/.../GraphQL/ReleasedDocumentLockGraphQlTests.cs` (the real engine lock behind the real mutations, test
  type `ReleasedDocumentTestInvoice` with the range `ReleasedDocumentTestInvoiceNumber`).

## Validate a CK model draft without importing it: `Models/ValidateCk`

`POST {tenantId}/v1/Models/ValidateCk` (multipart member `file`, policy `TenantAssetApiReadOnlyPolicy`, request limit 20 MB) answers
**200** with `CkModelValidationResponseDto` for every draft, valid or not (`isValid`, `modelId`, `sourceKind` `Source|Compiled`,
`errors`/`warnings` = `CkModelValidationMessageDto`: `messageNumber`, `level`, `file`, `elementPath`, `message`, `parameters`; `isBlocked`,
`blockers`); 400 only without a file. Backs the MCP tool `validate_ck_model` (SDK `IAssetServicesClient.ValidateCkModelAsync`).
Code: `Services/CkModelValidation/CkModelDraftValidator` (registered transient in `RuntimeEngineBuilderExtensions`), `CkModelFileReader`.

- **No second validator.** A zip that contains `ckModel.yaml` (the shallowest one marks the folder) is a source draft: it is extracted
  to a temp folder and compiled with the engine's `ICompilerService.CompileInMemoryAsync` (dependencies from the configured catalogs),
  every other upload is read by `CkModelFileReader`, the reader `ImportCk` uses for compiled uploads too. The
  compiled model then goes through the engine's `ITenantContext.ValidateCkModelAsync` (octo-construction-kit-engine-mongodb: the import's
  pre-write checks, read-only) and, without errors, `EvaluateFileImportBlockersAsync` (the guard of `ImportCk`).
- **Nothing is written**: no cache stream, no import command, no tenant write; the temp folder is deleted.
- **A bad draft is a finding, not a failed call**: compiler errors, YAML syntax errors, an unreadable file, a missing dependency are
  errors in the list. Every number is from the engine's message catalog (`MessageCodes.json`), none outside it: 147
  `CkModelDraftUnreadable` (`parameters.detail`, asset repository), 145 `CkModelSystemReferenceMismatch` (`modelId`,
  `referencedModelId`, `installedModelId`) and 146 `CkModelDependencyNotResolvable` (`modelId`, `detail`) from the engine, besides
  91 and 126. An unknown dependency of a **source** draft is message 1 (no `file`: the compiler names the draft folder itself) plus
  146 with `detail` only (asset repository, `IsDependencyResolutionFailure`; the model id is not known before the compile).
- **Zip safety**: entries with `..` or an absolute path are refused, at most 2,000 entries and 50 MB unpacked.
- **File and element**: `file` is relative to the `ConstructionKit/` folder, `null` for the folder itself (a server path and the temp folder
  name `octo-ck-validate-<guid>` never leave the service, under any spelling such as `/private/var` on macOS; temp paths are also
  stripped from the message text and the parameters), `elementPath` is built from the message placeholders (type/record/enum/interface/model, then attribute/
  role/method).
- **Source or compiled** is decided by `ckModel.yaml` alone: an upload that is a zip by content type, by a `.zip` name or by its zip header
  (`MayBeSourceZip`) is probed for it; without it the upload is a compiled model. The same rule holds for `ImportCk` (below).
- Tests: `UnitTests/Services/CkModelValidation/CkModelDraftValidatorTests`, `UnitTests/TenantApi/ModelsControllerValidateCkTests`,
  `IntegrationTests/Tenant/CkModelDraftValidationTests` (real compiler, catalogs, tenant and MongoDB: valid source folder not imported, unknown
  attribute with file and element, missing dependency of a compiled model and of a source draft, broken YAML, unreadable file, zip slip;
  every case asserts that the serialized response contains no temp folder name or temp path).

## Validate a blueprint draft without installing it: `blueprints/validate`

`POST {tenantId}/v1/blueprints/validate` (multipart member `file`, policy `TenantAssetApiReadOnlyPolicy`, request limit 20 MB, the
same as `Models/ValidateCk`) answers **200** with `BlueprintDraftValidationResponseDto` for every draft, valid or not (`isValid`,
`blueprintId`, `totalFindings`, `truncated`, `findings` = `BlueprintDraftFindingDto`: `code`, `level`, `file`, `modelId`, `message`,
`parameters`); 400 only without a file (or an empty one). Backs the MCP tool `validate_blueprint` (SDK
`IAssetServicesClient.ValidateBlueprintAsync`). Code: `Services/BlueprintValidation/BlueprintDraftValidationService` (registered
transient in `RuntimeEngineBuilderExtensions`); a host without the registration answers 500 "not configured" and writes nothing.

- **One validator.** The service runs the engine's `IBlueprintDraftValidator`, the same one `octo-bpm validate|pack|publish` and the
  install path (`ApplyBlueprintAsync`) use, with the CK model dependency-range check against all CK model catalogs the asset
  repository reads. Rules and codes: octo-construction-kit-engine `docs/blueprints.md`, "Validating a blueprint draft".
- **Upload**: a name ending in `.yaml`/`.yml` (any case) is read as the single `blueprint.yaml`; everything else is read as a zip of the
  blueprint folder (the shallowest `blueprint.yaml` marks the root). The zip is read in memory, never extracted to disk; the engine's
  file source refuses entries with `..` or an absolute path and limits the number of files and the unpacked size.
- **Nothing is written**: no install, no catalog write, no tenant write.
- **A bad draft is a finding, not a failed call**: codes 149-167 of the construction kit message catalog; an unreadable zip is 167.
- **Bounded**: at most 200 findings (`BlueprintDraftValidationResponseDto.MaxFindings`); `totalFindings` counts all, `truncated` says
  that some were left out.
- Tests: `UnitTests/Services/BlueprintDraftValidationServiceTests`, `UnitTests/TenantApi/BlueprintsControllerValidateTests` (result
  passed through, 400 without a file, 500 without the service, read-only policy, size limit),
  `IntegrationTests/Tenant/BlueprintDraftValidationHostTests` (the host resolves the service; a garbage upload is 167).

## Import a source draft: `Models/ImportCk` compiles a zipped `ConstructionKit/` folder

`POST {tenantId}/v1/Models/ImportCk` accepts a compiled model (json, yaml or a zip of one) **or** a zip of the source `ConstructionKit/`
folder, so the draft that `ValidateCk` judged can be imported as it is (MCP `validate_ck_model` then `import_ck_model`).

- **One source path.** `ImportCk` calls `ICkModelDraftValidator.TryCompileSourceAsync`: the same `MayBeSourceZip` / `ckModel.yaml` decision,
  the same extraction (`ExtractSource`: zip slip, 2,000 entries, 50 MB) and the same compiler call (`CompileSourceZipAsync` ->
  `ICompilerService.CompileInMemoryAsync`) as `ValidateAsync`; there is no second extraction or compile path. `null` = no source draft: the
  upload is read as a compiled model exactly as before (regression tests in `ModelsControllerServiceManagedImportTests`,
  `ModelsControllerUpgradeGuardEnforcementTests`, `ModelsControllerImportCkSourceTests`).
- **Compile errors** are refused **before anything is cached or queued**: `400`, `application/problem+json`,
  `code` `CK_MODEL_COMPILE_FAILED` (`CkModelCompileFailedError`), `message` and `detail` (an English summary with up to 10 findings), `fileName`,
  `errors` / `warnings` = the `CkModelValidationMessageDto` list `ValidateCk` reports (message number, file relative to `ConstructionKit/`,
  element path; no server path or temp folder name). An unsafe source zip, or one whose entries cannot be extracted, is such a finding too; a
  zip whose directory cannot be read at all is no source draft and gets the `400` of the compiled read, as before. Only the compile half runs
  here; the tenant checks run in the import job as for every import.
- **The compiled model is what is judged and imported**: the service-managed refusal (`403 CK_SERVICE_MANAGED_MODEL`) and the upgrade guard
  (`409 CK_UPGRADE_BLOCKED`, `forceBreakingUpdate`) judge it, and it is serialized to JSON and cached (`<modelId>.json`), so the import job
  (octo-bot-services, unchanged) reads a compiled model as it always did. Other entries of the zip are never imported.
- Tests: `UnitTests/TenantApi/ModelsControllerImportCkSourceTests` (compiled and queued, folder deleted, decision by `ckModel.yaml` whatever
  the content type, compile errors 400 with findings and nothing cached, zip slip, System model refused, 409 and forced import, compiled
  uploads unchanged), `IntegrationTests/Tenant/CkModelDraftValidationTests` (real compiler and tenant: validate then `ImportCk` the same
  zip, the cached model imported the way the job does it is in the tenant's CK collections; an invalid draft is refused with the findings
  `ValidateCk` reports).

## Export entry points run as the caller

`POST {tenantId}/v1/Models/ExportRtByQuery` and `ExportRtByDeepGraph` (`ModelsController`, policy
`TenantAssetApiReadOnlyPolicy`) hand the export to a bus command that the job service (octo-bot-services) runs as a Hangfire job.
The job must run with the data permissions of the caller, so the controller builds the caller context with the mapping the GraphQL
surface uses (`GraphQL.Helpers.GetSecurityContext(ClaimsPrincipal)`: subject from `sub` / NameIdentifier / `client_id`, roles from
the identity's role claims and the `role` claim) plus the `client_id` claim, and sends it in the additive optional members
`InitiatedBySubjectId`, `InitiatedByRoleNames`, `InitiatedByClientId` of `ExportRtByQueryCommandRequest` /
`ExportRtByDeepGraphCommandRequest` (octo-common-services).

- **Fail closed:** an unauthenticated principal, or one with neither `sub` nor `client_id`, gets `401` and no job is enqueued
  (`ModelsController.TryGetExportCaller`). A client-credentials token without `sub` is evaluated as its client with the client's
  roles, exactly like on the normal API; a service token is never treated as the system.
- The consumer (octo-bot-services) refuses a request without subject and fails an old job without context; the engine filters rows
  and prunes the deep graph (octo-construction-kit-engine-mongodb). Roll out asset repository and bot services in the same train:
  an old asset repository against the new job service gets an error (never an unfiltered export); a new asset repository against
  an OLD job service is the unsafe pair (the old job ignores the new members and still runs as the system), so the bot services must
  be rolled out first or in the same train, never later.
- Callers of the routes (SDK `AssetServicesClient`, octo-cli `ExportRtByQuery` / `ExportRtByDeepGraph`, MCP `CkModelFileTools`, Studio
  `asset-repo.service.ts`, pipelines/scripts) need no code change; restricted users now get fewer rows, and tokens without identity get 401.
- Tests: `ModelsControllerExportCallerTests` (subject/roles/client in the command, client-credentials subject, 401 without identity,
  nothing enqueued).

## Former files migration: System.Reporting → System.Files

The one-time move of System.Reporting 2.x file data to System.Files (sweep at tenant start, straggler timer, status
endpoint for the pre-check, own configuration section and Helm switch) ran on test-2,
staging-1, prod-1 and prod-2 and was **removed**. The last release that contains it is
**r3.5.32**; recovery path for an environment that still holds legacy Reporting file data (old dump, restore): run
that release, or take the code from the git history at that tag. The data the migration left in tenant databases is
**kept and read by nothing**: collections `FilesMigrationConflicts` (parked legacy documents, evidence),
`FilesMigrationAudit` and `FilesMigrationLease`. Dropping them is a separate decision. Still in use from that work:
`ITenantMongoDatabaseProvider` (raw tenant database access, used by the name lease) and `SystemAdministratorGuard`. The
removal is pinned by `tests/AssetRepositoryServices.UnitTests/SystemApi/RemovedLegacyFilesMoveTests.cs` (no status
route mapped, i.e. 404; no type of the former `Services/Files/Migration` namespace).

## CK Model Catalog REST API

System-scoped REST endpoints for browsing and managing Construction Kit model catalogs. Implemented in `CkModelCatalogController`.

| Method | Endpoint | Auth Policy | Description |
|--------|----------|-------------|-------------|
| `GET` | `system/v1/ckmodelcatalog` | ReadOnly | List all models from all catalogs (paged) |
| `GET` | `system/v1/ckmodelcatalog/search?q={term}` | ReadOnly | Search models by term |
| `GET` | `system/v1/ckmodelcatalog/catalogs` | ReadOnly | List available catalog sources |
| `GET` | `system/v1/ckmodelcatalog/{catalogName}` | ReadOnly | List models from specific catalog |
| `HEAD` | `system/v1/ckmodelcatalog/{modelId}` | ReadOnly | Check if model exists |
| `GET` | `system/v1/ckmodelcatalog/{catalogName}/{modelId}` | ReadOnly | Get model details |
| `GET` | `system/v1/ckmodelcatalog/{catalogName}/{modelId}/types?filter=&skip=&take=` | ReadOnly | Types (attributes, associations), association roles and enums of a catalog model version, without importing it. Paged: types sorted by id, `take` default 20, capped at 50; `filter` matches type id, description or attribute names; descriptions cut at 300 characters; attributes from a dependency carry no `valueType`; ids are named as in a tenant (`Basic/Equipment`, `System/ParentChild`). Refusals are `application/problem+json` with `code` `InvalidCkModelId` (400, the id needs a version), `CkCatalogNotFound` or `CkCatalogModelNotFound` (404); `CkCatalogUnavailable` (503) when the catalog cannot be read (unreachable, rate limit, timeout) - no answer about the model, retry later. |
| `POST` | `system/v1/ckmodelcatalog/refresh` | DataModelManagement | Refresh all catalog caches |
| `POST` | `system/v1/ckmodelcatalog/{catalogName}/refresh` | DataModelManagement | Refresh specific catalog cache |

**DTOs:** `DataTransferObjects/CkModelCatalog/` - `CkModelCatalogDto`, `CkModelCatalogItemDto`, `CkModelCatalogListResponseDto`

**Delegates to:** `ICatalogService` from `ConstructionKit.Contracts` (registered via `AddConstructionKit()`).

## Blueprint Catalog REST API (System API)

System-scoped REST endpoints for browsing and refreshing blueprint catalogs. Implemented in `SystemApi/v1/Controllers/BlueprintsController.cs`.

| Method | Endpoint | Auth Policy | Description |
|--------|----------|-------------|-------------|
| `GET` | `system/v1/blueprints` | ReadOnly | List all blueprints from all catalogs (paged) |
| `GET` | `system/v1/blueprints/search?q={term}` | ReadOnly | Search blueprints by term |
| `GET` | `system/v1/blueprints/catalogs` | ReadOnly | List available catalog sources |
| `HEAD` | `system/v1/blueprints/{blueprintId}` | ReadOnly | Check if blueprint exists |
| `GET` | `system/v1/blueprints/{blueprintId}` | ReadOnly | Get blueprint details |
| `POST` | `system/v1/blueprints/catalogs/refresh` | DataModelManagement | Refresh all blueprint catalog caches |
| `POST` | `system/v1/blueprints/catalogs/{catalogName}/refresh` | DataModelManagement | Refresh a specific blueprint catalog cache (case-insensitive name) |

The refresh endpoints always perform a **forced** refresh (bypassing the engine's 60s cache-file TTL
and the GitHub unchanged-remote-timestamp short-circuit) and return a
`BlueprintCatalogRefreshResponseDto` with one entry per catalog (`Status`: `Refreshed`, `Skipped` or
`Failed` plus an optional message). A failing catalog does not abort the refresh of the others; an
unknown catalog name yields `404` with a `NotFoundErrorDto`.

**DTOs:** `DataTransferObjects/Blueprints/` - `BlueprintCatalogRefreshResponseDto`, `BlueprintCatalogRefreshResultDto`

**Delegates to:** `IBlueprintCatalogManager` from `ConstructionKit.Engine` (registered via `AddConstructionKit()`).
