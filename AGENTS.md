# AGENTS.md

Instructions for coding agents in this repository. Detail lives in `docs/`; read the matching file before changing the
code it covers.

## What this repo is

This is the **Octo Asset Repository Services** - a multi-tenant ASP.NET Core 10.0 backend service that provides GraphQL
and REST API access to data products in the Octo Mesh ecosystem. The service manages runtime entities, construction
kit models, and time-series stream data stored in MongoDB.

**Key Technologies:**
- .NET 10.0 (ASP.NET Core Web API)
- GraphQL (GraphQL.NET v8.8.5) with dynamic schema generation per tenant
- MongoDB via Octo Runtime Engine
- Multi-tenancy with tenant-specific schemas and databases
- Docker containerization
- Azure Pipelines CI/CD

### Solution Layout
- **Octo.AssetRepServices.sln** - Main solution file
  - **src/AssetRepositoryServices/** - Main web service project
  - **src/AssetRepositoryServices.Resources/** - Localized resource strings for GraphQL descriptions
  - **tests/AssetRepositoryServices.UnitTests/** - Unit tests, no containers
  - **tests/AssetRepositoryServices.IntegrationTests/** - Integration tests against MongoDB and CrateDB containers
    (its `README.md` describes fixtures, collections and the test categories)
  - **tests/AssetRepositoryServices.MethodCanaryScan/** - Method canary scan against a running local stack, not part of
    the CI test run (`docs/method-in-process.md`)

### Build Configurations
- **DebugL** - **[DEFAULT]** Local development configuration (version 999.0.0, uses local NuGet packages from
  ../nuget) - **ALWAYS use this for local development**
- **Debug** - Standard debug configuration
- **Release** - Production release configuration (used by CI/CD only)

## Read before you change

<!-- >>> generated: routing -->
| When you change | Read first |
|---|---|
| `**/SystemApi/**`, `**/TenantApi/**`, `**/Consumers/**`, `**/Diagnostics/**`, `**/DefaultConfigurationCreatorService.cs`, `**/ScratchTenantMarkerStore.cs` | `docs/api-controllers.md` |
| `**/Program.cs`, `**/Configuration/**`, `src/**/GraphQL/**`, `Directory.Build.props` | `docs/architecture.md` |
| `**/Controllers/BlueprintsController.cs`, `**/DataTransferObjects/Blueprints/**`, `**/BlueprintUpdateBlankingFlowTests.cs`, `**/BlueprintsMutation.cs` | `docs/blueprint-update-blanking.md` |
| `**/BlueprintLock/**`, `**/CkModelValidation/**`, `**/CkModelCatalog/**`, `**/CkModelCatalogController.cs`, `**/BlueprintsController.cs`, `**/ModelsController.cs` | `docs/ck-model-api.md` |
| `**/UpgradeGuard/**`, `**/*CkUpgrade*.cs`, `**/Services/Ck*.cs`, `**/CkModelLibraryStatus*.cs`, `**/CkModelCatalogController.cs`, `**/BlueprintsMutation.cs` | `docs/ck-model-import.md` |
| `**/Types/Meta/**`, `**/Types/Rt*.cs`, `**/Mapping/**`, `**/OctoBuilder.cs`, `**/RecordMember*.cs`, `**/HiddenAttribute*.cs`, `**/*WriteGuard*.cs`, `**/AccessQuery*.cs` | `docs/ck-v2.md` |
| `**/GraphQL/Methods/**`, `**/*ParentTenant*.cs`, `**/OneMethodCallPerRequestRule.cs`, `**/CkV2/*MethodMutation*Tests.cs`, `**/CkV2/AsyncMethod*Tests.cs` | `docs/method-gateway.md` |
| `**/Services/Methods/**`, `**/GraphQL/MethodHandlers/**`, `**/MethodHandlersQuery*.cs`, `**/*MethodIdempotency*Tests.cs` | `docs/method-idempotency-audit.md` |
| `**/GatewayOwnedMethod*.cs`, `**/GenericCreate*.cs`, `**/OctoSessionAmbient*.cs`, `**/CkMethodTargetAccess*.cs`, `tests/*MethodCanaryScan/**`, `**/Adversarial/**` | `docs/method-in-process.md` |
| `**/Files/**`, `**/Controllers/File*.cs`, `**/Controllers/LargeBinaries*.cs`, `**/FilesAccessMetrics.cs` | `docs/platform-file-system.md` |
| `**/RtEntityByInterfaceTypes.cs`, `**/RuntimeQueryGuard*.cs`, `**/RuntimeModelQuery.cs`, `**/RtTransientQuery.cs`, `**/InterfaceQuery*.cs`, `tests/**/ByInterface*.cs` | `docs/runtime-queries.md` |
| `**/Secrets/**`, `**/GraphQL/SecretsQuery.cs`, `**/GraphQL/RtMutationBase.cs`, `**/OctoSecretStateDtoType.cs`, `**/Utils/Secret*.cs` | `docs/secret-attributes.md` |
| `**/StreamData/Controllers/**`, `**/ConfigureStreamDataConfiguration.cs`, `tests/**/StreamData/**` | `docs/stream-data-management.md` |
| `**/GraphQL/StreamData*.cs`, `**/Types/StreamData*.cs`, `**/StreamData/Methods/**`, `**/PersistedQueryLookup.cs`, `**/DataPermissionStreamGuard.cs` | `docs/streamdata-graphql.md` |
| `**/Security/**`, `**/ConfigureJwtBearer*.cs`, `**/*Authorization*.cs`, `**/StreamDataReadRoleGate.cs`, `**/OctoApplicationBuilder*.cs` | `docs/tenant-enforcement.md` |
<!-- <<< end generated: routing -->

Background, not path-triggered; each file is linked from the routed doc of its topic: `docs/principal-kind.md`
(how the caller's principal kind is set and checked), `docs/runtime-query-guard.md` (operator view of the unbounded
query guard: configuration, metric, error `ASSET1007`), `docs/method-idempotency-store.md` (records, atomic claim,
expiry, latency), `docs/gateway-owned-method-handlers.md` (how to register an in-process method handler) and
`docs/gridfs-orphan-sweep.md` (endpoint and runbook of the orphan sweep).

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

DebugL restores the upstream `999.0.0` packages (engine, common-services, SDK) from `../nuget`. On a fresh workspace, or
when the restore reports a missing `Meshmakers.Octo.*` `999.0.0` package, build the dependency chain first with
`Invoke-BuildAll -configuration DebugL` or `Invoke-BuildRange` (octo-tools). Their exit code does not show a failed
upstream build: continue only when the summary reports `Percentage of successful builds: 100%`.

### Test
```bash
# Run all unit tests (no containers)
dotnet test tests/AssetRepositoryServices.UnitTests/ -c DebugL

# Run all integration tests (needs Docker)
dotnet test tests/AssetRepositoryServices.IntegrationTests/ -c DebugL

# Local full suite without the load-sensitive tests (pre-commit check, recorded verification)
dotnet test Octo.AssetRepServices.sln -c DebugL --filter "Category!=LoadSensitive"

# The load-sensitive tests on their own, on a quiet machine
dotnet test tests/AssetRepositoryServices.IntegrationTests/ -c DebugL --filter "Category=LoadSensitive"

# Run specific test class
dotnet test --filter "FullyQualifiedName~RtEntityDeleteMutationTests" -c DebugL
```

The CrateDB containers of the integration tests run `crate:6.4.6`. `OCTO_TEST_CRATEDB_IMAGE=crate:5.10.10` runs the
same suite on another supported CrateDB line; every supported line must stay green.
The dump and restore between the lines is the version matrix (`Category=CrateVersionMatrix`, see the README of the
test project).

Tests whose result depends on the speed of the machine carry `[Trait("Category", "LoadSensitive")]` next to their own
category: the wall-clock budget `ByInterfacePerformanceTests` (`Performance`) and
`RollupRecomputeStatementBudgetTests.ARecomputeOfALargeRange_StaysInsideTheStatementBudget` (`StatementBudget`, a
one-second CrateDB budget per statement). On a busy machine (other builds or suites running) they fail without a code
change, so the local full suite leaves them out and they run on their own. The CI runs every test, these included. The
trait is shared with `octo-construction-kit-engine-mongodb` and `octo-identity-services`; details in the README of the
integration test project.

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
  --build-arg OCTO_CORE_LIB_VERSION=<version> \
  --build-arg OCTO_COMM_VERSION=<version> \
  -t octo-asset-repo-services .
```

## Before you commit

1. `dotnet build Octo.AssetRepServices.sln -c DebugL` succeeds with zero warnings
   (`TreatWarningsAsErrors=true` in `Directory.Build.props`).
2. The unit tests and the integration tests report 0 failed; CI runs the same two projects. Locally the full suite runs
   with `--filter "Category!=LoadSensitive"` (third test command above); run the load-sensitive tests on their own when
   you change the code they measure. The integration tests start MongoDB and CrateDB containers and need Docker.
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
  service carries `IAllowParentTenantAdministration`; the GraphQL endpoint carries only the request-scoped
  `IAllowParentTenantAdministrationRequest` (`docs/tenant-enforcement.md`, `docs/method-gateway.md`).
- **Build a caller's security context only through `GetSecurityContext`**, never `RtSecurityContext.ForUser` without the
  principal kind (`docs/tenant-enforcement.md`, `docs/principal-kind.md`).
- **Do not add a guard call inside a runtime-query resolver;** wrap the field in `RuntimeQueryGuardSchemaVisitor`
  (`docs/runtime-queries.md`).
- **Never log a method call's target id, parameters or caller token,** also not in refusals (`docs/method-gateway.md`).
- **GraphQL types are cached and reused, so they must be thread-safe.** Use the UTC `DateTime` scalar, never the stock
  `DateTimeGraphType` (`docs/architecture.md`).
- **Secret attributes are never returned,** neither plaintext nor the stored envelope (`docs/secret-attributes.md`).
