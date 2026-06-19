# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is the **Octo Asset Repository Services** - a multi-tenant ASP.NET Core 9.0 backend service that provides GraphQL and REST API access to data products in the Octo Mesh ecosystem. The service manages runtime entities, construction kit models, and time-series stream data stored in MongoDB.

**Key Technologies:**
- .NET 9.0 (ASP.NET Core Web API)
- GraphQL (GraphQL.NET v8.6.0) with dynamic schema generation per tenant
- MongoDB via Octo Runtime Engine
- Multi-tenancy with tenant-specific schemas and databases
- Docker containerization
- Azure Pipelines CI/CD

## Build and Test Commands

**IMPORTANT: Always use the DebugL configuration for local development builds.**

### Build
```bash
# Restore NuGet packages
dotnet restore Octo.AssetRepServices.sln

# Build solution - ALWAYS use DebugL for local development
dotnet build Octo.AssetRepServices.sln -c DebugL

# DebugL configuration:
# - Uses version 999.0.0
# - Uses local NuGet packages from ../nuget
# - Preferred for all local development and testing
```

### Test
```bash
# Run all integration tests
dotnet test tests/AssetRepositoryServices.IntegrationTests/ -c DebugL

# Run all tests except SystemTests
dotnet test '**/*Tests.csproj' --exclude '**/*SystemTests.csproj' -c DebugL

# Run specific test class
dotnet test --filter "FullyQualifiedName~RtEntityDeleteMutationTests" -c DebugL
```

### Run Locally
```bash
# Run the application from the main project with DebugL configuration
dotnet run --project src/AssetRepositoryServices/AssetRepositoryServices.csproj -c DebugL
```

### Docker
```bash
# Build Docker image (requires build args for private NuGet)
docker build -f src/AssetRepositoryServices/Dockerfile \
  --build-arg OCTO_PRIVATE_NUGET_SERVICE=<nuget-url> \
  --build-arg OCTO_PRIVATE_NUGET_CERTIFICATE=<cert-path> \
  --build-arg OCTO_VERSION=<version> \
  -t octo-asset-repo-services .
```

## Project Structure and Architecture

### Solution Layout
- **Octo.AssetRepServices.sln** - Main solution file
  - **src/AssetRepositoryServices/** - Main web service project
  - **src/AssetRepositoryServices.Resources/** - Localized resource strings for GraphQL descriptions

### Build Configurations
- **DebugL** - **[DEFAULT]** Local development configuration (version 999.0.0, uses local NuGet packages from ../nuget) - **ALWAYS use this for local development**
- **Debug** - Standard debug configuration
- **Release** - Production release configuration (used by CI/CD only)

### Key Architectural Components

#### 1. Multi-Tenant GraphQL Engine
The service dynamically generates GraphQL schemas per tenant:

- **SchemaContext** (`GraphQL/Caches/SchemaContext.cs`) - Caches tenant-specific GraphQL schemas in memory (limit: 64 schemas)
- **OctoQuery** (`GraphQL/OctoQuery.cs`) - Root query with three main sections:
  - `ConstructionKit` - Query construction kit models (CK types, enums, attributes)
  - `Runtime` - Query runtime entities (data instances)
  - `StreamData` - Query time-series data via persisted (`streamDataQuery`) and transient (`transientStreamDataQuery`) entry points
- **OctoMutation** (`GraphQL/OctoMutation.cs`) - Root mutation for Runtime and ConstructionKit operations

#### StreamData Query Surface

Three execution shapes, all archive-driven (per CK Archive snapshot, which carries the target CK type and the captured column list):

| Entry point | Use case |
|---|---|
| `streamData.streamDataQuery(rtId)` | Execute a persisted `RtStreamDataQuery`. Loaded entity holds `ArchiveRtId`, columns, filters, sort, time range. The resolver dispatches to the correct repo method based on the loaded subtype (`RtSimpleSdQuery` / `RtAggregationSdQuery` / `RtGroupingAggregationSdQuery` / `RtDownsamplingSdQuery`). |
| `streamData.transientStreamDataQuery` | Ad-hoc execution without persistence. Four sub-connections: `simple`, `aggregation`, `groupingAggregation`, `downsampling`. The server derives `ckTypeId` from the archive snapshot (no client argument). |

Both surfaces accept runtime overrides on the `rows` / sub-connection level:

- `arg: StreamDataArguments` — override time range and limit
- `sortOrder: [Sort]` — override sort order
- `fieldFilter: [FieldFilter]` — **additional** filters, **AND-combined** with the persisted `FieldFilter` server-side (`StreamDataQueryDtoType.MergeFilters`). Same merging pattern as the runtime `RtQuery.Rows.fieldFilter` argument.

The persisted query's `aggregations` sub-connection takes a `ResultAggregationInput` (count/min/max/avg/sum × attribute paths) and runs a second engine call over the same data set as `rows` — useful for "give me the avg/max alongside the rows" without persisting an aggregation-typed query.

GraphQL types are dynamically created based on the tenant's construction kit model stored in MongoDB.

#### StreamData Mutations

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

All custom mutations use `ResolveConnectionContextExtensions.HandleException` to surface
domain exceptions (`InvalidArchiveStateTransitionException`, `ArchiveNotFoundException`,
`RollupSourceMissingException`, …) as stable GraphQL error codes with the underlying message
in `error.extensions.OctoDetails`.

#### StreamData Setup

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

#### 2. Multi-Tenant Request Pipeline
- **TenantIdRouteConstraint** (`Routing/TenantIdRouteConstraint.cs`) - Routes include tenant ID
- **TenantUserContextBuilder** (`GraphQL/RequestHandling/TenantUserContextBuilder.cs`) - Builds tenant context from HTTP request
- **GraphQLUserContext** (`GraphQL/Utils/GraphQLUserContext.cs`) - Per-request context containing tenant info
- **TenantDocumentExecutor** (`GraphQL/RequestHandling/TenantDocumentExecutor.cs`) - Executes GraphQL queries in tenant context

#### 3. API Controllers
Located in versioned API folders:

**System APIs** (`SystemApi/v1/Controllers/`):
- `DiagnosticsController.cs` - Health and diagnostics
- `BlueprintsController.cs` - Blueprint management
- `CkModelCatalogController.cs` - CK model catalog browsing, search, and cache refresh

**Tenant APIs** (`TenantApi/v1/Controllers/`):
- `TenantsController.cs` - Tenant management (each tenant manages its own child tenants)
- `ModelsController.cs` - Construction kit and runtime model import/export (includes `ImportFromCatalog` endpoint)
- `LargeBinariesController.cs` - Binary file download. Falls back to magic-byte sniffing via `BinaryContentTypeDetector` when the stored `ContentType` is missing or `application/octet-stream` (legacy data uploaded before detection existed). For non-seekable source streams the head bytes are re-prepended via `PrependedReadStream`.
- `DiagnosticsController.cs` - Per-tenant diagnostics. `GET slow-mongo-queries` returns the recent in-memory `SlowQueriesBuffer` entries filtered by `Database == tenantId` (AB#4212); backs the Refinery Studio Diagnostics → Slow Queries page.

#### 4. Stream Data Management
Time-series data support (`StreamData/`):
- **StreamDataController** - REST API for stream data operations
- **TenantManager** - Manages stream data tenant contexts
- **StreamDataDatabaseManager** - Database operations for time-series data
- **StreamDataTenantContext** - Per-tenant stream data context

#### 5. Configuration and DI
- **Program.cs** - Application startup with NLog, observability, authentication (JWT + OIDC)
- **RuntimeEngineBuilderExtensions** - Configures Octo runtime engine, GraphQL, authentication
- **OctoApplicationBuilderExtensions** - Middleware pipeline configuration
- Configuration sections:
  - `System` - System-level configuration
  - `AssetRepository` - Asset repository specific settings

#### 6. Dynamic Type System
GraphQL types are generated dynamically based on Construction Kit models:
- **RtEntityMutationGeneric** - Generic mutations for runtime entities (create, update, delete)
- **RtEntityGenericAssociation** - Dynamic association/relationship types
- **DynamicConnectionType** - Relay-style pagination connections
- **DynamicEdgeType** - Relay-style edges
- **CkTypeDtoType**, **CkAttributeDtoType**, **CkEnumDtoType** - Construction kit metadata types

Delete operations support multiple strategies via `DeleteOptions`:
- `Archive` (default) - Soft delete
- `Permanent` - Hard delete

### Important Naming Conventions
- **Ck** prefix = Construction Kit (metadata/model definitions)
- **Rt** prefix = Runtime (actual data instances)
- **Dto** suffix = Data Transfer Objects
- Types like `CkId` have been renamed to `RtCkId` in runtime model contexts

### Dependencies
External Octo services referenced via `$(OctoVersion)`:
- `Meshmakers.Octo.Services.Infrastructure` - Core infrastructure
- `Meshmakers.Octo.Services.Observability` - Telemetry and observability
- `Meshmakers.Octo.Services.StreamData` - Stream data support
- `Meshmakers.Octo.Services.Swagger` - OpenAPI/Swagger configuration
- `Meshmakers.Octo.Runtime.Engine.MongoDb` - MongoDB runtime engine

Version resolution (from `Directory.Build.props`):
- DebugL configuration: uses version 999.0.0 and local NuGet at `$(OctoRepoRootPath)../nuget`
- With private server: uses version 0.1.*
- Public: uses version 3.2.*

## Development Notes

### N:M Association Query Columns
N:M associations are exposed as query columns with `::totalCount` (INT64) and `::exists` (BOOLEAN).

**Query column resolution** (`RtQueryRowDtoType.CreateRtSimpleQueryCellDto`):
- Detects N:M columns via `AssociationTuple.Multiplicity == N`
- Counts `RtEntityGraphItem.Associations` by `NavigationPropertyName`
- Returns `int` for totalCount, `bool` for exists

**Filter handling** (`RtQueryDtoType.ResolveRtQueryRowsAsync`):
- Extracts `::` field filters from `queryOptions` before they reach `RtFieldFilterResolver`
- Converts `exists` filters to count comparisons (e.g., `exists == true` → `count >= 1`)
- Sets `AssociationCountFilter` on `NavigationPair` for MongoDB-level filtering
- Determines association direction (inbound/outbound) from CK model

### When Working with GraphQL
- Schema caching is automatic per tenant (up to 64 cached schemas)
- Schema invalidation happens via `SchemaContext.Invalidate(tenantId)`
- All GraphQL types must be thread-safe (they're cached and reused)
- Use resource strings from `AssetRepositoryServices.Resources` for descriptions

### When Working with Tenant Context
- Always access tenant via `Helpers.GetTenantContext(arg.UserContext)`
- Session management via `arg.GetSessionAccessor().Session`
- Tenant repositories are obtained from tenant context: `tenantContext.GetTenantRepository()`

### When Adding New Operations
- Place mutations in appropriate mutation classes (RtMutation, CkMutation)
- Use `OperationResult` and call `ResolveConnectionContextExtensions.ValidateOperationResult()` for consistency
- Handle exceptions via `arg.HandleException(e)` in GraphQL resolvers

### Authentication
The service supports dual authentication:
- Cookie-based authentication for GraphQL Playground
- JWT Bearer tokens for API access
- OIDC integration via `InfrastructureCommon.OidcAuthenticationScheme`

### Configuration
Use environment variable prefix `OCTO_` to override configuration values.
User secrets are supported for local development (UserSecretsId: `173d8e91-b831-4e8a-a43f-672c57e6a4da`).

### CK Model Catalog REST API

System-scoped REST endpoints for browsing and managing Construction Kit model catalogs. Implemented in `CkModelCatalogController`.

| Method | Endpoint | Auth Policy | Description |
|--------|----------|-------------|-------------|
| `GET` | `system/v1/ckmodelcatalog` | ReadOnly | List all models from all catalogs (paged) |
| `GET` | `system/v1/ckmodelcatalog/search?q={term}` | ReadOnly | Search models by term |
| `GET` | `system/v1/ckmodelcatalog/catalogs` | ReadOnly | List available catalog sources |
| `GET` | `system/v1/ckmodelcatalog/{catalogName}` | ReadOnly | List models from specific catalog |
| `HEAD` | `system/v1/ckmodelcatalog/{modelId}` | ReadOnly | Check if model exists |
| `GET` | `system/v1/ckmodelcatalog/{catalogName}/{modelId}` | ReadOnly | Get model details |
| `POST` | `system/v1/ckmodelcatalog/refresh` | ReadWrite | Refresh all catalog caches |
| `POST` | `system/v1/ckmodelcatalog/{catalogName}/refresh` | ReadWrite | Refresh specific catalog cache |

**DTOs:** `DataTransferObjects/CkModelCatalog/` - `CkModelCatalogDto`, `CkModelCatalogItemDto`, `CkModelCatalogListResponseDto`

**Delegates to:** `ICatalogService` from `ConstructionKit.Contracts` (registered via `AddConstructionKit()`).

### CK Model Import from Catalog (Tenant API)

Tenant-scoped endpoint to import a CK model directly from a catalog without file upload.

| Method | Endpoint | Auth Policy | Description |
|--------|----------|-------------|-------------|
| `POST` | `{tenantId}/v1/models/ImportFromCatalog` | ReadWrite | Import CK model from catalog into tenant |

**Request body:**
```json
{
  "catalogName": "PublicGitHub",
  "modelId": "Energy-2.0.0"
}
```

**Response:** `TransferModelResponseDto` with `jobId` for async tracking.

**Flow:** Fetches compiled model from catalog → serializes to JSON → caches in Redis → sends `ImportCkCommandRequest` through existing async job pipeline (RabbitMQ → Hangfire → `ITenantContext.ImportCkModelAsync`).

| `POST` | `{tenantId}/v1/models/ResolveDependencies` | ReadOnly | Resolve dependency tree for a catalog model against tenant |

**Request body:** Same as ImportFromCatalog (`catalogName` + `modelId`).

**Response:** `DependencyResolutionResponseDto` with recursive `RootModel` tree. Each item contains: `modelId`, `name`, `requiredVersion`, `installedVersion` (null if not installed), `action` ("install", "none"), and nested `dependencies`.

| `POST` | `{tenantId}/v1/models/CheckUpgrade` | ReadOnly | Pre-flight check for migration impact |

**Request body:** Same as ImportFromCatalog (`catalogName` + `modelId`).

**Response:** `UpgradeCheckResponseDto` with: `modelName`, `installedVersion`, `targetVersion`, `upgradeNeeded`, `migrationPathAvailable`, `hasBreakingChanges`, `errorMessage`.

### Authorization: Data Model Management

Write operations on CK model catalogs (import, refresh) are gated by the `DataModelManagementPolicy`. This requires the `octo_api.data_model_management` or `octo_api` scope claim.

| Endpoint | Policy |
|----------|--------|
| Catalog browsing (GET) | `SystemAssetApiReadOnlyPolicy` |
| Catalog refresh (POST) | `DataModelManagementPolicy` |
| ImportFromCatalog | `DataModelManagementPolicy` |
| ResolveDependencies | `TenantAssetApiReadOnlyPolicy` |
| CheckUpgrade | `TenantAssetApiReadOnlyPolicy` |