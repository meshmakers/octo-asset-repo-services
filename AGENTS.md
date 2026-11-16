# AGENTS.md

Instructions for coding agents in this repository. Detail lives in `docs/`; read the matching file before changing the
code it covers.

## What this repo is

This is the **Octo Asset Repository Services** - a multi-tenant ASP.NET Core 9.0 backend service that provides GraphQL
and REST API access to data products in the Octo Mesh ecosystem. The service manages runtime entities, construction
kit models, and time-series stream data stored in MongoDB.

**Key Technologies:**
- .NET 9.0 (ASP.NET Core Web API)
- GraphQL (GraphQL.NET v8.6.0) with dynamic schema generation per tenant
- MongoDB via Octo Runtime Engine
- Multi-tenancy with tenant-specific schemas and databases
- Docker containerization
- Azure Pipelines CI/CD

### Solution Layout
- **Octo.AssetRepServices.sln** - Main solution file
  - **src/AssetRepositoryServices/** - Main web service project
  - **src/AssetRepositoryServices.Resources/** - Localized resource strings for GraphQL descriptions

### Build Configurations
- **DebugL** - **[DEFAULT]** Local development configuration (version 999.0.0, uses local NuGet packages from
  ../nuget) - **ALWAYS use this for local development**
- **Debug** - Standard debug configuration
- **Release** - Production release configuration (used by CI/CD only)

## Read before you change

<!-- >>> generated: routing -->
| When you change | Read first |
|---|---|
| `**/SystemApi/**`, `**/TenantApi/**`, `**/Consumers/**`, `**/Services/DefaultConfigurationCreatorService.cs` | `docs/api-controllers.md` |
| `**/Program.cs`, `**/Configuration/**`, `**/GraphQL/Caches/**`, `**/GraphQL/RequestHandling/**`, `**/GraphQL/RtMutationBase.cs`, `Directory.Build.props` | `docs/architecture.md` |
| `**/Controllers/BlueprintsController.cs`, `**/DataTransferObjects/Blueprints/**`, `**/BlueprintUpdateBlankingFlowTests.cs` | `docs/blueprint-update-blanking.md` |
| `**/BlueprintLock/**`, `**/Services/CkModelValidation/**`, `**/CkModelCatalogController.cs`, `**/Controllers/BlueprintsController.cs`, `**/ModelsController.cs` | `docs/ck-model-api.md` |
| `**/UpgradeGuard/**`, `**/Services/*Ck*Guard.cs`, `**/Services/Ck*Resolver.cs`, `**/Services/CkModelLibraryStatusService.cs`, `**/CkUpgradeBlockedGraphQlError.cs` | `docs/ck-model-import.md` |
| `**/GraphQL/Types/Meta/**`, `**/GraphQL/Mapping/**`, `**/GraphQL/OctoBuilder.cs`, `**/GraphQL/RecordMemberPreservation.cs`, `**/Utils/*WriteGuard.cs`, `**/AccessQueryGuard.cs` | `docs/ck-v2.md` |
| `**/GraphQL/Methods/CkMethod*.cs`, `**/OneMethodCallPerRequestRule.cs`, `**/CkV2/*MethodMutation*Tests.cs` | `docs/method-gateway.md` |
| `**/Services/Methods/**`, `**/GraphQL/MethodHandlers/**`, `**/GraphQL/MethodHandlersQuery.cs`, `**/CkV2/MethodIdempotencyKeyTests.cs` | `docs/method-idempotency-audit.md` |
| `**/GatewayOwnedMethodHandlers.cs`, `**/GenericCreateReplacedBy*.cs`, `**/OctoSessionAmbientStateProbe.cs`, `tests/*MethodCanaryScan/**` | `docs/method-in-process.md` |
| `**/Services/Files/**`, `**/DataTransferObjects/Files/**`, `**/Controllers/File*.cs`, `**/Controllers/LargeBinaries*.cs`, `tests/*IntegrationTests/Files/**` | `docs/platform-file-system.md` |
| `**/RtEntityByInterfaceTypes.cs`, `**/Utils/RuntimeQueryGuard*.cs`, `**/RuntimeQueryGuardOptions.cs`, `tests/*IntegrationTests/GraphQL/ByInterface/**` | `docs/runtime-queries.md` |
| `**/Secrets/**`, `**/GraphQL/SecretsQuery.cs`, `**/Utils/Secret*.cs` | `docs/secret-attributes.md` |
| `**/StreamData/Controllers/**`, `**/ConfigureStreamDataConfiguration.cs`, `tests/*IntegrationTests/StreamData/**` | `docs/stream-data-management.md` |
| `**/GraphQL/StreamData*.cs`, `**/StreamData/Methods/**` | `docs/streamdata-graphql.md` |
| `**/Security/**`, `**/ConfigureJwtBearerOptions.cs`, `**/*AuthorizationPolicies.cs`, `**/StreamDataReadRoleGate.cs`, `**/OctoApplicationBuilderExtensions.cs` | `docs/tenant-enforcement.md` |
<!-- <<< end generated: routing -->

Background, not path-triggered; each file is linked from the routed doc of its topic: `docs/principal-kind.md`,
`docs/runtime-query-guard.md`, `docs/method-idempotency-store.md`, `docs/gateway-owned-method-handlers.md`,
`docs/gridfs-orphan-sweep.md`.

## Build & test

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

The CrateDB containers of the integration tests run `crate:6.4.6`. `OCTO_TEST_CRATEDB_IMAGE=crate:5.10.10` runs the
same suite on another supported CrateDB line (AB#7149); every supported line must stay green.
The dump and restore between the lines is the version matrix (`Category=CrateVersionMatrix`, see the README of the
test project).

Tests must be locale-independent (developer machines run e.g. `de_AT`, CI runs `en_US`): parse and
format numbers with `CultureInfo.InvariantCulture` (e.g. `Convert.ToDouble(raw, CultureInfo.InvariantCulture)`
on a JSON `GetRawText()` value). Verify a culture-sensitive test with
`LANG=de_AT.UTF-8 LC_ALL=de_AT.UTF-8 dotnet test --filter ... -c DebugL`.

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

## Before you commit

1. `dotnet build Octo.AssetRepServices.sln -c DebugL` succeeds with zero warnings
   (`TreatWarningsAsErrors=true` in `Directory.Build.props`).
2. The test commands above report 0 failed. The integration tests start MongoDB and CrateDB containers and need Docker.
3. Update the doc under `docs/` that covers the code you changed (the routing table above names it), in the same commit.
   Behaviour that API clients or operators see also goes into `CHANGELOG.md` under `## Unreleased`, as the existing
   entries do.
4. If you touched `AGENTS.md` or any file in `docs/`: `Test-OctoAgentDocs -Fix` reports 0 errors.
   It rewrites the generated routing table, so run it before you stage, not after.

## Rules

- **This repository is public.** No work item ids, internal hostnames, customer or tenant names, or secrets in code
  comments, docs or test names.
- **A new endpoint that changes a tenant's data model carries `DataModelManagementPolicy`** and is added to
  `DataModelManagementAuthorizationTests.ModelChangingEndpoints` (`docs/tenant-enforcement.md`).
- **A new stream-data read resolver calls `EnsureStreamReadAllowedAsync`** (`docs/tenant-enforcement.md`).
- **One configurator owns the JWT bearer scheme; call `AddJwtBearer()` without an argument.** No endpoint of this
  service carries `IAllowParentTenantAdministration` (`docs/tenant-enforcement.md`).
- **Build a caller's security context only through `GetSecurityContext`**, never `RtSecurityContext.ForUser` without the
  principal kind (`docs/tenant-enforcement.md`, `docs/principal-kind.md`).
- **Do not add a guard call inside a runtime-query resolver;** wrap the field in `RuntimeQueryGuardSchemaVisitor`
  (`docs/runtime-queries.md`).
- **Never log a method call's target id, parameters or caller token,** also not in refusals (`docs/method-gateway.md`).
- **GraphQL types are cached and reused, so they must be thread-safe.** Use the UTC `DateTime` scalar, never the stock
  `DateTimeGraphType` (`docs/architecture.md`).
- **Secret attributes are never returned,** neither plaintext nor the stored envelope (`docs/secret-attributes.md`).
