---
description: Multi-tenant GraphQL engine, request pipeline, DI and configuration, naming, dependencies, and the rules for GraphQL resolvers and mutations.
applies_to: **/Program.cs, **/Configuration/**, src/**/GraphQL/**, Directory.Build.props
---

# Architecture and GraphQL development notes

## Key Architectural Components

### 1. Multi-Tenant GraphQL Engine
The service dynamically generates GraphQL schemas per tenant:

- **SchemaContext** (`GraphQL/Caches/SchemaContext.cs`) - Caches tenant-specific GraphQL schemas in memory (limit: 64 schemas)
- **OctoQuery** (`GraphQL/OctoQuery.cs`) - Root query with three main sections:
  - `ConstructionKit` - Query construction kit models (CK types, enums, attributes)
  - `Runtime` - Query runtime entities (data instances)
  - `StreamData` - Query time-series data via persisted (`streamDataQuery`) and transient (`transientStreamDataQuery`) entry points
- **OctoMutation** (`GraphQL/OctoMutation.cs`) - Root mutation for Runtime and ConstructionKit operations

### 2. Multi-Tenant Request Pipeline
- **TenantIdRouteConstraint** (octo-common-services, `Infrastructure/Routing/`) - Routes include
  tenant ID; registered with `builder.Services.AddOctoTenantIdRouteConstraint()`. The local copy
  was removed — it also wrote an untyped `HttpContext.Items["d"]` that nothing read.
- **TenantUserContextBuilder** (`GraphQL/RequestHandling/TenantUserContextBuilder.cs`) - Builds tenant context from HTTP request
- **GraphQLUserContext** (`GraphQL/Utils/GraphQLUserContext.cs`) - Per-request context containing tenant info
- **TenantDocumentExecutor** (`GraphQL/RequestHandling/TenantDocumentExecutor.cs`) - Executes GraphQL queries in tenant context

### 5. Configuration and DI
- **Program.cs** - Application startup with NLog, observability, authentication (JWT + OIDC)
- **RuntimeEngineBuilderExtensions** - Configures Octo runtime engine, GraphQL, authentication
- **OctoApplicationBuilderExtensions** - Middleware pipeline configuration
- Configuration sections:
  - `System` - System-level configuration
  - `AssetRepository` - Asset repository specific settings

**Tenant setup failure handling.** `DefaultConfigurationCreatorService` wires two durable stores from the
engine: `ITenantLifecycleStore` (asset-repo is its single writer, recording each tenant's
Creating/Active/Failed state and driving the reconcile pass) and `ITenantSetupRetryStore` (a
setup run that throws is recorded and re-run in the background instead of being lost with the process
that observed it). Both are drained from `RetryFailedTenantsAsync` on the 30 s
`FailedTenantRetryLoop` timer (octo-common-services, `octo-common-services/docs/tenant-setup.md`). Note that a tenant reaching lifecycle state `Active` means
the Identity service confirmed **and** seeded its default configuration — the identity
consumer reports `SuccessIdentityDataSeedPending` while the tenant still has no roles, which keeps the
tenant in `Creating` instead of marking it provisioned.

Operational: `PUT {tenantId}/v1/tenants/clearCache?childTenantId=<child>` (octo-cli `ClearCache`)
publishes `PreUpdateTenant` + `PosUpdateTenant` and thereby makes **every** service re-run its tenant
setup — the cheapest way to recover a half-provisioned tenant without restarting pods.

### 6. Dynamic Type System
GraphQL types are generated dynamically based on Construction Kit models:
- **RtEntityMutationGeneric** - Generic mutations for runtime entities (create, update, delete)
- **RtEntityGenericAssociation** - Dynamic association/relationship types
- **DynamicConnectionType** - Relay-style pagination connections
- **DynamicEdgeType** - Relay-style edges
- **CkTypeDtoType**, **CkAttributeDtoType**, **CkEnumDtoType** - Construction kit metadata types

Delete operations support multiple strategies via `DeleteOptions`:
- `Archive` (default) - Soft delete
- `Permanent` - Hard delete

## Important Naming Conventions
- **Ck** prefix = Construction Kit (metadata/model definitions)
- **Rt** prefix = Runtime (actual data instances)
- **Dto** suffix = Data Transfer Objects
- Types like `CkId` have been renamed to `RtCkId` in runtime model contexts

## Dependencies
Octo packages of `AssetRepositoryServices.csproj`, all pinned with `$(OctoCoreLibVersion)`:
- `Meshmakers.Octo.Services.Infrastructure` - Core infrastructure
- `Meshmakers.Octo.Services.Observability` - Telemetry and observability
- `Meshmakers.Octo.Services.Swagger` - OpenAPI/Swagger configuration
- `Meshmakers.Octo.Services.Methods`, `Meshmakers.Octo.Services.Methods.MongoDb` - CK method dispatch and its MongoDB stores
- `Meshmakers.Octo.Services.Notifications` - Notifications
- `Meshmakers.Octo.Communication.Contracts` - Communication contracts
- `Meshmakers.Octo.Runtime.Engine`, `Meshmakers.Octo.Runtime.Engine.MongoDb` - Runtime engine on MongoDB
- `Meshmakers.Octo.Runtime.Engine.CrateDb` - Stream data on CrateDB
- `Meshmakers.Octo.ConstructionKit.Models.System.Files` - `System.Files` CK model

Version resolution (from `Directory.Build.props`):
- DebugL configuration: uses version 999.0.0 and local NuGet at `$(OctoRepoRootPath)../nuget`
- With private server: uses version 0.1.*
- Otherwise: no fallback — versions come only from the pipeline; a non-DebugL build without `-p:OctoVersion=X.Y.Z` fails fast with `OCTO0001`
- `OctoCoreLibVersion` and `OctoCommVersion` are set by the pipeline and fall back to `OctoVersion` when unset

## N:M Association Query Columns
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

## When Working with GraphQL
- **DateTime scalar is `UtcDateTimeGraphType`** (`GraphQL/Types/Scalars/`), not the stock
  GraphQL.NET `DateTimeGraphType`. It keeps the wire name `DateTime` but normalizes
  every instant to `DateTimeKind.Utc` before serialization (Unspecified is *labelled* UTC —
  the platform convention — never shifted), so responses always carry the ISO-8601 `Z`
  designator. The stock scalar formats kind-dependently inside `Serialize` and would emit
  naive strings for Unspecified-kind values from DB read paths. Never reference the stock
  `DateTimeGraphType` in field/argument declarations (two scalars named `DateTime` also break
  schema init); `OctoSchema` maps CLR `DateTime` to the UTC scalar for auto-mapped fields.
  `SimpleScalarType.Serialize` applies the same normalization to top-level DateTime cell values.
- Schema caching is automatic per tenant (up to 64 cached schemas)
- Schema invalidation happens via `SchemaContext.Invalidate(tenantId)` (on `PosUpdateTenant` / `PreDeleteTenant`) **and**
  whenever the tenant's CK cache was unloaded or reloaded since the schema was built: the cached schema carries
  the marker of the CK cache load it was populated from (`CkCacheGeneration`, the type collection instance that is replaced
  on every reload, the same marker as the hidden-name cache), and `GetOrCreateAsync` rebuilds when it no longer matches.
  This covers the reloads that send no tenant event - the stale-type fallback, the display-rule sweeper - and a
  `PosUpdateTenant` that was published before this process listened to the bus (service start). An unchanged cache costs
  one dictionary lookup and a reference compare per request. Tests: `GraphQL/Schema/SchemaFollowsCkCacheTests`,
  `UnitTests/GraphQL/CkCacheGenerationTests`.
- **Schema build vs. CK cache unload (AB#8998):** a `PreUpdateTenant` (CK import, revalidation) or the stale-type fallback can
  unload the tenant's CK cache while `GetOrCreateAsync` builds the schema - after `PopulateAsync` loaded it and before
  `Initialize`. The build then fails with a `CkCacheException` ("CkCache for tenant 'x' not found"). When such an exception is
  in the chain **and** the CK cache is no longer the load the build started from (`CkCacheGeneration.HasChanged`), the build
  is repeated once from a fresh load, with a WARN `GraphQL schema build for tenant {TenantId} failed because the CK cache was
  unloaded or reloaded during the build; building once more from a fresh load (AB#8998)`. A second failure becomes the usual
  `SchemaCreationFailed` error; a CK error on a stable cache (a genuinely missing type) is not retried. The build log line
  carries `attempt {Attempt}`. Seen on prod-2 (tenant sbeg, 2026-11-15: two HTTP 500 within 1.5 s of the unload). Tests:
  `GraphQL/Schema/SchemaBuildSurvivesCkCacheUnloadTests` (test seam `SchemaContext.AfterPopulateForTesting` unloads the cache
  between populate and initialize).
- The schema is **initialized eagerly** inside `SchemaContext.GetOrCreateAsync` (not lazily on the first request),
  so an invalid schema is never cached and the build time is measured completely. Every build logs at Information:
  `GraphQL schema for tenant {TenantId} built in {ElapsedMs} ms ({TypeCount} types, {InterfaceCount} CK interfaces,
  {MethodCount} CK methods; populate {PopulateMs} ms, initialize {InitializeMs} ms, attempt {Attempt})`
- **Exception details in GraphQL errors** (`extensions.details`: exception text, inner exceptions, stack trace) are
  exposed only in the `Development` environment or with the explicit opt-in `GraphQl:ExposeExceptionDetails`
  (`OCTO_GraphQl__ExposeExceptionDetails`; an explicit value wins over the environment) — `GraphQlErrorExposure`.
  Messages, `extensions.code`/`codes` and custom extensions (`OctoDetails`, `attributePath`, `operation`)
  are unchanged. Containers run as `Production` (the Helm charts and the test-2 values do not set
  `ASPNETCORE_ENVIRONMENT`). CK v2 access errors (`ATTRIBUTE_NOT_WRITABLE` / `ATTRIBUTE_NOT_QUERYABLE`) carry code,
  attribute path and operation but never a value in any environment: entity-selector values in the path are redacted
  (`[passwordHash=…]`). Tests: `GraphQlErrorExposureTests` (unit),
  `GraphQL/Errors/GraphQlErrorDetailsTests` (integration, Production and Development host fixtures).
  `HiddenAttributeAccessException.RedactSelectorValues` replaces everything from the first `=` inside a
  selector to the **last** `]` of the path (the mesh adapter's `RtAttributeAccessGuard.WithoutSelectorValues` rule), but only
  when the rest after that `]` is a valid path suffix of the engine grammar (`->name`, `.name`, `::meta` or nothing; review
  finding on #134); otherwise (an unterminated selector, `[passwordHash='AQ]AA-secret`, or no `]` at all) to the end of the
  path. The path is also redacted to its end when a quoted value is still open at that `]` (an odd number of `'` or `"`
  between the `=` and the `]`, `[passwordHash='AQ]->AAsecret`; the quote check of the mesh adapter, or a quote
  still open when the quotes are read in order, `[x='a"b' "secret].c`, review of #136), even if the rest looks like a valid suffix. A value containing `]` used to leave its rest in the message, because the old regex ended at the first `]`. Segments between the first selector value and the last `]` are not shown either
  (`a.t[x=1].b.u[y=2]->c` → `a.t[x=…]->c`). A value with `[` is no valid engine path (`RtPathEvaluator.TokenizePath` refuses it),
  so the selector-key regex of `AccessQueryGuard` needs no change. Tests: `CkAttributeAccessSchemaTests`
  (`AccessErrors_RedactTheWholeSelectorValue_*`, all three factory methods), `StoredQueryHiddenAttributeTests.SelectorValueWithBrackets_*`,
  `StoredQueryHiddenAttributeTests.SelectorValueWithOpenQuoteAtLastBracket_*`.
- All GraphQL types must be thread-safe (they're cached and reused)
- Use resource strings from `AssetRepositoryServices.Resources` for descriptions

## When Working with Tenant Context
- Always access tenant via `Helpers.GetTenantContext(arg.UserContext)`
- Session management via `arg.GetSessionAccessor().Session`
- Tenant repositories are obtained from tenant context: `tenantContext.GetTenantRepository()`

## When Adding New Operations
- Place mutations in appropriate mutation classes (RtMutation, CkMutation)
- Use `OperationResult` and call `ResolveConnectionContextExtensions.ValidateOperationResult()` for consistency
- Handle exceptions via `arg.HandleException(e)` in GraphQL resolvers. `HandleException` maps known
  exception types to stable GraphQL error codes with their real message; `OctoGraphQLException` and
  `CkEnumValueNotFoundException` are surfaced with their message (code `GraphQlModelValidationErrors`)
  instead of the generic `"An error occurred"`. Any other exception type is still masked —
  add a branch if a resolver needs to surface a client-facing message.

## Generic Runtime Mutation Attribute Handling (`RtMutationBase`)

The generic `runtime.runtimeEntities.create/update` path (`RtEntityMutationGeneric` →
`RtMutationBase.RtEntityFromInputObjectAsync` → `TryHandleAttributeAsync`) sets attributes by calling
`RtTypeWithAttributes.SetAttributeValue` **directly** — it does **not** go through
`RtPathEvaluator.SetValue` (only the RtQuery-row update path in `QueryMapper` does). This means every
attribute-value-type coercion that lives in `RtPathEvaluator.SetValueByPath` must be mirrored in
`TryHandleAttributeAsync`'s `switch`, or the generic path stores raw client values verbatim.

- **Enum** (`AttributeValueTypesDto.Enum`): `TryHandleAttributeAsync` resolves the value to
  the integer enum key via `ResolveEnumKey` (name → key case-insensitively, or a whole-number key),
  validating against the CK enum and throwing `OctoGraphQLException.EnumValueNotFound` /
  `InvalidEnumValueType` on bad input. Before the fix, a name string (e.g. `"NEW"`) was stored
  verbatim instead of its key `0`, silently corrupting enum fields and breaking downstream integer
  filters. Note the generic GraphQL SimpleScalar path can box a numeric key as `double`, so numeric
  coercion accepts every whole-number CLR type — not just `int` (which is all `RtPathEvaluator`
  handles). The same `TryHandleAttributeAsync` is reused by `HandleRecordAsync`, so enum attributes
  inside records are covered too.

## Configuration
Use environment variable prefix `OCTO_` to override configuration values.
User secrets are supported for local development (UserSecretsId: `173d8e91-b831-4e8a-a43f-672c57e6a4da`).
