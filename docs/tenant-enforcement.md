---
description: Caller principal kind, JWT bearer wiring, tenant enforcement for user and service tokens, data model management and stream-data read authorization.
applies_to: **/Security/**, **/ConfigureJwtBearerOptions.cs, **/*AuthorizationPolicies.cs, **/StreamDataReadRoleGate.cs, **/OctoApplicationBuilderExtensions.cs
---

# Authentication and authorization

## Principal kind of the caller (AB#8705)

`GraphQL.Helpers.GetSecurityContext` (the one place GraphQL and REST build the caller's `RtSecurityContext`) sets
`PrincipalKind` via `Security/CallerPrincipalKind.Resolve`: `act` or `amr` `delegation`/`impersonation` → `Client`
(delegated tokens are Client, Odin 2026-11-10), else `sub` → `Interactive`, else `client_id` → `Client`, else
`Unknown`. `ModelsController.ImportRt` sends it as `ImportRtCommandRequest.InitiatedByPrincipalKind` (bot services pass it
into `RtImportCallerContext`). Only CK types with a `writeAccess` block (engine AB#8574) look at it. Any new route that
opens a caller session must build its context through `GetSecurityContext`, never `RtSecurityContext.ForUser` without the
kind (that is `Unknown` and refused by restricted types). Rule, compatibility and tests: `docs/principal-kind.md`.

## Authentication
The service supports dual authentication:
- Cookie-based authentication for GraphQL Playground
- JWT Bearer tokens for API access
- OIDC integration via `InfrastructureCommon.OidcAuthenticationScheme`

### The tenant gate only started working in AB#5054

Everything in the next section describes a middleware that, until AB#5054, **never ran a single
check in this service**. `TenantAuthorizationMiddleware` inspects only principals whose
`Identity.AuthenticationType` reads `Bearer` — its guard against false 403s on the cookie principal
this service issues for the GraphQL playground. That label is not the scheme name; it comes from
`TokenValidationParameters.AuthenticationType`, which the JWT handler leaves at the framework default
`AuthenticationTypes.Federation` unless the host sets it. This service did not, so the `Use…`/`Add…`
pair gated nothing: no user token was ever checked, and the AB#5032 service-token audit log — the
inventory an operator is supposed to read before flipping to `Enforce` — was empty because nothing
wrote to it, not because nothing was wrong.

Three pieces make it work now, and each fails silently on its own:

| Where | What | Why it matters |
|---|---|---|
| `Configuration/ConfigureJwtBearerOptions.cs` | `TokenValidationParameters.AuthenticationType = "Bearer"` | 🔴 The silent-no-op trap above. Extracted from the former inline `AddJwtBearer(options => …)` delegate — which also removed a `builder.Services.BuildServiceProvider()` call — so the settings are unit-testable and match how identity, bot, communication-controller, MCP and AI services do it. |
| `Configuration/DependencyInjection/RuntimeEngineBuilderExtensions.cs` | `ConfigureOptions<ConfigureJwtBearerOptions>()`, and `AddJwtBearer()` **without an argument** | 🔴 The options factory runs configurators in registration order. A delegate on `AddJwtBearer` runs last, and an assignment `options.TokenValidationParameters = new …` there discards the label again — compiling, and with the configurator's own unit test still green. octo-ai-services shipped a release in exactly that state (AB#5051 → AB#5056). |
| `Program.cs` | `AddOctoTenantAuthorization(o => o.UserTokenEnforcement = LogOnly)` **before** `AddOctoTenantAuthorization(builder.Configuration)` | The user path is armed in stages, see below. Registered in that order so configuration still wins; pinned against the real `Program.cs` source (AB#6832). |

`tests/AssetRepositoryServices.UnitTests/Configuration/TenantAuthorizationWiringTests.cs` pins all
three, including a source-level guard that no second configurator of the bearer scheme appears —
because a test of the configurator class alone provably proves nothing here. The registration order of
`Program.cs` itself is pinned in `ProgramTenantGatePinTests` (AB#6832), and `TenantGatePipelineTests` proves the
refusal end to end.

### Tenant enforcement for user tokens is staged (AB#5054)

`TenantAuthorizationOptions.UserTokenEnforcement` (`Disabled` does not exist; `LogOnly` | `Enforce`,
platform default `Enforce`) is set to **`LogOnly`** in this service's `Program.cs`, i.e. no request
outcome changes and every access an enforcing run would refuse is logged with subject, client id and
both tenants (`... This would be denied with UserTokenEnforcement=Enforce (AB#5054)`).

**The flip is configuration-only; the rollback is LogOnly (AB#6728).** The code default is registered
**before** `AddOctoTenantAuthorization(builder.Configuration)`, and `Program.cs` reads
`AddEnvironmentVariables("OCTO_")`, so `OCTO_TENANTAUTHORIZATION__USERTOKENENFORCEMENT=Enforce` (section
`TenantAuthorization:UserTokenEnforcement`, set from the chart value) wins without a code change or a
release. Rollback = set the value to `LogOnly` again. With `Enforce`, a bearer user token whose `tenant_id`
differs from the route tenant (or has none) gets **403** with the JSON body `{"message": ...}` and neither the
GraphQL resolver nor the controller action runs. Not covered by the gate: SignalR hubs (not in this service),
routes without a tenant segment, `[AllowAnonymous]` endpoints, cookie principals and requests without a bearer
header. No endpoint of this service carries `IAllowParentTenantAdministration`, and none may: the marker would
give a parent tenant the data of its children.

Tests (AB#6832, `tests/AssetRepositoryServices.UnitTests/Configuration/`):
`TenantGatePipelineTests` drives requests through the real `UseOctoTenantAuthorization()` pipeline and the real
`{tenantId:tenantId}` route constraint (in-process `TestServer`; only the Bearer-labelled authentication handler
and a one-field GraphQL schema / one-action controller are stand-ins) for GraphQL `tenants/{tenantId}/graphQl` and a
REST route `{tenantId}/v1/...`: foreign tenant 403 and handler not run, matching tenant passes, missing
`tenant_id` 403, and the LogOnly code default lets the same foreign request through. `ProgramTenantGatePinTests`
pins the registration order in the real `Program.cs` source, the pipeline order, that `Enforce` wins from the
configuration section and from the real environment variable name, and that no endpoint carries the
parent-tenant marker.

**Known cross-tenant callers (inventory, AB#6728, Dash0 LogOnly warnings of test-2 and staging-1).** They decide
when an environment may flip, not the code:

| Caller | Finding | Outcome |
|---|---|---|
| `octo-cli` / SDK | Calls with the context's root-tenant token against `-tid <tenant>` routes; most of the LogOnly volume | No exemption: authenticates per target tenant, AB#6836 (gates the test-2 flip) |
| Refinery Studio | Sends a token of one tenant to another tenant's route in some paths | No exemption: Studio bug AB#6731, to be released before staging-1 / prod flips |
| `energy-community-app` | User tokens of tenants `energytest`/`eegroider`/`mrotest` against the host tenant's GraphQL | Open owner decision (Frigg/Sol); recommendation: no exemption, the app obtains a token for the host tenant |
| `meshmakers-app` | Topology and capability probes with the user's token against the root tenant's route; **not seen** on test-2 or staging-1 | No exemption unless its need is evidenced; the app already degrades on a 403 |

Child-tenant admin operations on `TenantsController` pass the child in a **query parameter** on the caller's own
route, so the gate never sees them: this service has no route where `{tenantId}` is a foreign subject.

### Tenant enforcement for service tokens (AB#5032 / AB#5047)

The pipeline runs the shared `TenantAuthorizationMiddleware` (`UseOctoTenantAuthorization()` in
`Configuration/OctoApplicationBuilderExtensions.cs`, after `UseAuthorization()`), which matches the
route tenant against the caller's `tenant_id` claim. How it treats **client-credentials** tokens is
operator-settable, and `Program.cs` binds that setting with
`builder.Services.AddOctoTenantAuthorization(builder.Configuration)` — section `TenantAuthorization`,
i.e. **`OCTO_TENANTAUTHORIZATION__SERVICETOKENENFORCEMENT`** = `Disabled` | `LogOnly` (default) |
`Enforce`, plus `OCTO_TENANTAUTHORIZATION__CROSSTENANTSERVICECLIENTIDS__0…` for the allow-list.

The `Add…` call is what makes the variable take effect; the `Use…` call alone runs on the built-in
defaults (AB#5047 — asset-repo, bot-services and MCP were in exactly that state, so an estate-wide
flip to `Enforce` would have left them on `LogOnly`). Every OctoMesh service that hosts the
middleware — Identity, Communication Controller, Asset-Repo, Bot, MCP — binds the same section
through the same helper, so one value set fleet-wide reaches all of them. Semantics and the
`Enforce` rollout rules live in `octo-common-services/CLAUDE.md`.

Note this service runs with `ValidateAudience = false`, which is why the tenant match is the only
transport-level barrier between a client-credentials client of the authority and a foreign tenant.

## Authorization: Data Model Management (role-based since AB#8529)

Every operation that changes a tenant's data model is gated by `DataModelManagementPolicy`: scope `octo_api` **and the tenant role
`DataModelManagement`** (central `AddTenantRolePolicy` of octo-common-services, same pattern as `TenantAdministrationPolicy`; both
`role` claim spellings are probed). Until AB#8529 the policy was scope-only (`octo_api` or `octo_api.data_model_management`), which
every token passed; `octo_api.data_model_management` alone is no longer enough. A refusal is `403` with the body
`{"message": "...", "code": "DataModelManagementRequired"}` (message names the missing scope or role); nothing is changed.
The role is carried by the group `TenantOwners` (bootstrap seed), so tenant creators, provisioned administrators
(`ProvisionCurrentUser`, admin provisioning mappings) and scratch-tenant creators have it; any other user or client-credentials
client needs it granted (`AddUserToRole`, or membership in a group that carries it). **Breaking change for external clients.**

| Endpoint | Policy |
|----------|--------|
| `POST {tenant}/v1/Models/ImportCk` (incl. `forceBreakingUpdate`) | `DataModelManagementPolicy` (was read-write until AB#8529) |
| `POST {tenant}/v1/Models/ImportFromCatalog`, `ImportFromCatalogBatch` (octo-cli `FixAll`, incl. `forceBreakingUpdate`) | `DataModelManagementPolicy` |
| `POST {tenant}/v1/blueprints/apply`, `POST blueprints/updates/apply`, `DELETE blueprints/{name}`, `POST blueprints/{name}/uninstall/preview` (AB#8966 dry run) | `DataModelManagementPolicy` (was read-write until AB#8529) |
| GraphQL `blueprints { install, applyUpdate, uninstall }` | roles `AdminPanelManagement` **and** `DataModelManagement` (field level; missing `DataModelManagement` = error code `DataModelManagementRequired`) |
| `POST system/v1/ckmodelcatalog/refresh`, `ckmodelcatalog/{name}/refresh` | `DataModelManagementPolicy` |
| `POST system/v1/blueprints/catalogs/refresh`, `blueprints/catalogs/{name}/refresh` | `DataModelManagementPolicy` |
| Catalog browsing (GET), blueprint history/current/updates/installations, `updates/preview` | read-only policies |
| ResolveDependencies, CheckUpgrade, LibraryStatus, ValidateCk | `TenantAssetApiReadOnlyPolicy` |

In-process paths do not pass the HTTP policy and are not affected: the services' own CK models and service-managed blueprints
(`DefaultConfigurationCreatorService*`, `ApplyServiceManagedBlueprintsAsync`, EnableAi, platform-services tenant mode), the
bot-services Hangfire job that executes a queued import (only reachable through the guarded controller actions). Tests:
`UnitTests/Configuration/DataModelManagementAuthorizationTests` (per endpoint: policy carried, read-write without role refused,
other admin roles not enough, role allowed, 403 body with code through a TestServer pipeline),
`UnitTests/GraphQL/BlueprintsMutationCkUpgradeBlockedTests` (GraphQL role check). A new model-changing endpoint must carry
`DataModelManagementPolicy` and be added to `DataModelManagementAuthorizationTests.ModelChangingEndpoints`.

## Authorization: stream-data reads (staged since AB#8559)

Every stream-data read resolver calls `DataPermissionStreamGuard.EnsureStreamReadAllowedAsync`; since AB#8559 it first runs
`GraphQL/Utils/StreamDataReadRoleGate`: the caller must hold one of the tenant roles `StreamDataReader`, `StreamDataWriter` or
`StreamDataAdmin` (both `role` claim spellings, `TenantRoleAuthorizationHandler.HasTenantRole`), then the data-policy check
(AB#4973) applies as before. Before AB#8559 the GraphQL endpoint (`AuthenticatedUserPolicyGraphApi`, authenticated user only)
let every token of the tenant read all stream data of types without a data policy.

| Setting | Env | Values |
|---------|-----|--------|
| `StreamDataReadAuthorization:RoleEnforcement` | `OCTO_STREAMDATAREADAUTHORIZATION__ROLEENFORCEMENT` | `Off` (unchanged), `Warn` (**default**), `Enforce` |
| `StreamDataReadAuthorization:WarnLogInterval` | `OCTO_STREAMDATAREADAUTHORIZATION__WARNLOGINTERVAL` | default `01:00:00`; `00:00:00` logs every read |

- Gated resolvers: transient `simple`, `aggregation`, `groupingAggregation`, `downsampling`, persisted `streamDataQuery` and its
  `rows` / `aggregations`. Not gated: archive metadata (`rollupsFor`, `coverageFor`, `recomputeJobsFor`, `archivesStorageStats`,
  `rollupQueryMetadata`, `resolveSeriesQuery`) and archive entity queries (runtime queries, data policies only).
- `Warn`: the read goes ahead; warning `Stream-data read without a stream-data role allowed by StreamDataReadRoleEnforcement=Warn:
  query {kind} in tenant {tenant} by subject {sub} / client {client_id} ... (AB#8559)`, once per caller, tenant and query kind
  per `WarnLogInterval`. Ids only, never token content.
- `Enforce`: GraphQL error code `StreamDataReadRoleRequired` (`AssetRepositoryServiceConstants.StreamDataReadRoleRequiredCode`,
  mapped in `HandleException`), message names the three roles; nothing is read.
- Always allowed: an `RtSecurityContext.IsSystem` context and an in-process execution without a principal (`GraphQlUserContext.User`
  null; the HTTP endpoint always has an authenticated principal). Pipelines run by the mesh adapter read archives in-process through
  the engine and never reach this gate; pipeline GraphQL calls run as the HTTP caller or the adapter's pipeline service account.
- **Before `Enforce`**: every caller in the warn log needs a stream-data role. Affected are only callers that read through the
  asset-repository API with their own token: app clients and service accounts with a client-credentials token, and users that read
  time series in Studio, Grafana, Excel or the solution frontends. Mesh-adapter pipelines (EC allocation, billing, simulation, ...)
  read in-process and are not affected (AB#8583 corrected the earlier note). Pipeline service accounts get `StreamDataReader`
  automatically: declarative accounts from the communication controller on reconcile (AB#8563,
  `PipelineServiceAccountProvisioningService`), legacy `octo-pipeline-sa-*` accounts from identity at tenant setup (AB#8581).
  A manual `AddClientToRole` is unnecessary only once both versions run and a reconcile or tenant setup has granted the role;
  until then a pipeline service account may lack `StreamDataReader` and its gated GraphQL reads fail under `Enforce`.
- Tests: `UnitTests/GraphQL/StreamDataReadRoleGateTests` (modes x roles, claim spellings, system context, warn window, error code),
  `IntegrationTests/StreamData/StreamDataReadRoleGateTests` (mode x caller x query kind through the GraphQL layer). A new
  stream-data read resolver must call `EnsureStreamReadAllowedAsync`.
