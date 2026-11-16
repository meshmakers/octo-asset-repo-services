---
description: Blueprint-locked entity errors, ValidateCk, export entry points, the removed files migration, and the CK model and blueprint catalog REST APIs.
applies_to: **/BlueprintLock/**, **/Services/CkModelValidation/**, **/CkModelCatalogController.cs, **/Controllers/BlueprintsController.cs, **/ModelsController.cs
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

## Validate a CK model draft without importing it: `Models/ValidateCk`

`POST {tenantId}/v1/Models/ValidateCk` (multipart member `file`, policy `TenantAssetApiReadOnlyPolicy`, request limit 20 MB) answers
**200** with `CkModelValidationResponseDto` for every draft, valid or not (`isValid`, `modelId`, `sourceKind` `Source|Compiled`,
`errors`/`warnings` = `CkModelValidationMessageDto`: `messageNumber`, `level`, `file`, `elementPath`, `message`, `parameters`; `isBlocked`,
`blockers`); 400 only without a file. Backs the MCP tool `validate_ck_model` (SDK `IAssetServicesClient.ValidateCkModelAsync`).
Code: `Services/CkModelValidation/CkModelDraftValidator` (registered transient in `RuntimeEngineBuilderExtensions`), `CkModelFileReader`.

- **No second validator.** A zip that contains `ckModel.yaml` (the shallowest one marks the folder) is a source draft: it is extracted
  to a temp folder and compiled with the engine's `ICompilerService.CompileInMemoryAsync` (dependencies from the configured catalogs),
  every other upload is read by `CkModelFileReader`, the reader `ImportCk` uses too (moved out of the controller, same behaviour). The
  compiled model then goes through the engine's `ITenantContext.ValidateCkModelAsync` (octo-construction-kit-engine-mongodb: the import's
  pre-write checks, read-only) and, without errors, `EvaluateFileImportBlockersAsync` (the guard of `ImportCk`).
- **Nothing is written**: no cache stream, no import command, no tenant write; the temp folder is deleted.
- **A bad draft is a finding, not a failed call**: compiler errors, YAML syntax errors, an unreadable file, a missing dependency are
  errors in the list. Numbers: the engine's message catalog; 8492 with `parameters.reason` `unreadable-draft` (asset repository),
  `system-reference-mismatch` / `dependency-resolution` (engine). An unknown dependency of a **source** draft is message 1 (no `file`:
  the compiler names the draft folder itself) plus 8492 with `dependency-resolution` (asset repository, `IsDependencyResolutionFailure`).
- **Zip safety**: entries with `..` or an absolute path are refused, at most 2,000 entries and 50 MB unpacked.
- **File and element**: `file` is relative to the `ConstructionKit/` folder, `null` for the folder itself (a server path and the temp folder
  name `octo-ck-validate-<guid>` never leave the service, under any spelling such as `/private/var` on macOS; temp paths are also
  stripped from the message text and the parameters), `elementPath` is built from the message placeholders (type/record/enum/interface/model, then attribute/
  role/method).
- `ImportCk` still does **not** compile a source zip (it reads the first `.json`/`.yaml` entry as a compiled model); only `ValidateCk` does.
- Tests: `UnitTests/Services/CkModelValidation/CkModelDraftValidatorTests`, `UnitTests/TenantApi/ModelsControllerValidateCkTests`,
  `IntegrationTests/Tenant/CkModelDraftValidationTests` (real compiler, catalogs, tenant and MongoDB: valid source folder not imported, unknown
  attribute with file and element, missing dependency of a compiled model and of a source draft, broken YAML, unreadable file, zip slip;
  every case asserts that the serialized response contains no temp folder name or temp path).

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
