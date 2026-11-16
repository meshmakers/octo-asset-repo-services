---
description: Gateway gate, typed method mutations, asynchronous methods with operation handles, parent-tenant administration and the method mutation resolver.
applies_to: **/GraphQL/Methods/**, **/*ParentTenant*.cs, **/OneMethodCallPerRequestRule.cs, **/CkV2/*MethodMutation*Tests.cs, **/CkV2/AsyncMethod*Tests.cs
---

# CK method gateway

## CK method gateway gate

`GraphQL/Methods/CkMethodGateway.cs` (`ICkMethodGateway`, registered by `AddCkMethodGateway()`; tests `GraphQL/Methods/CkMethodGatewayTests`)
is the one place that decides, **before the target entity is loaded**, whether a caller may call a CK method. Order: authenticate
(unauthenticated = no principal) -> authorize with the shared `ICkMethodAuthorizationEvaluator` of octo-common-services (roles any-of,
`allowSelf` on the parsed id from the request, declared scopes, `octo_api` always, default-deny; **one enforcement code path**, the pipeline filter at
band 200 stays as second line) -> load the target and check the data permission through the `CkMethodTargetAccessCheck` delegate the caller passes in (see `docs/method-in-process.md`) -> dispatch via
`ICkMethodDispatcher` with `CkMethodPrincipalScope` open. A refusal is `METHOD_FORBIDDEN` with `methodId` and the fixed reason `not-authorized`: it
depends on the method, the caller and the requested target id (for `allowSelf`), never on whether the target exists, so it is identical for an existing and a missing target, and the target is never read before the check
(`Refusal_IsByteIdentical_ForAnExistingAndAMissingTarget_AndReadsNothing` uses a counting check). `METHOD_TARGET_NOT_FOUND` is returned only to a caller
who passed the declared check. The refusal log line (Information) names method id, tenant and the rule, never the target id, parameters or token.
`octo_api` is required for every method call independent of `GraphQl:EnforceMutationScope` (the gate never reads the flag; the generic
`MutationScopeListener` is unchanged). `CkMethodGraphQlError.Create` turns a failed response into ONE `ExecutionError` with `extensions.code` and
`extensions.parameters` and a fixed message. The gateway also validates the parameters (see "Method mutation resolver" below). The only
production caller is the resolver of the typed method mutations (`CkMethodFieldResolver`).
**One method call per request:** `GraphQL/RequestHandling/OneMethodCallPerRequestRule.cs` is a GraphQL.NET `IValidationRule`
(registered with `AddValidationRule<>(useForCachedDocuments: true)` next to `MutationScopeListener` in `RuntimeEngineBuilderExtensions`). It judges only the
selected mutation operation (aliases, fragments, inline fragments resolved; `@skip`/`@include` not evaluated) and refuses a method field next to a generic
mutation or two method fields with ONE error `ONE_METHOD_CALL_PER_REQUEST` (`Statics.GraphQlOneMethodCallPerRequest`, `extensions.parameters`: `reason`,
`methodIds`), before any resolver runs. A method field is found by the `Statics.CkMethodCall` marker; which method counts is the single function
`OneMethodCallPerRequestRule.CountsAsMethodCall` (an asynchronous method counts as a method call; a decision on this is still open). Navigation fields of the mutation tree are object types the query
tree does not reach; every other field is a generic mutation. The code is a GraphQL-layer code, not part of the `METHOD_*` wire contract.

## Typed method mutations

Every concrete CK type with methods gets one mutation field per method **next to `create`/`update`** in its per-type mutation type
(`runtime { {type}s { {method}(rtId, input) { invocationId result } } }`). Code: `GraphQL/Methods/CkMethodMutationFields` (adds the fields, called from
`RtEntityMutation`), `CkMethodGraphTypes` (input and result type of one method), `CkMethodResponseMapper` + `CkMethodResultConverter` (gateway response -> value or error),
`CkMethodCallInfo` (the marker). `IGraphTypesCache.GetMethods(type)` reads `CkTypeGraph.AllMethods` (inherited and interface methods included, so a method appears
**once** on the derived concrete type) and `GetMethodTypes(method)` caches the types per qualified method id (shared by all derived types).

- **Field name** = `CkMethodIds.MethodClassName(methodId)` camelCase (`ResetPassword-2` -> `resetPassword2`). A name that equals an existing field of the type
  (`create`, `update`, another method) throws `OctoGraphQLException.CkMethodFieldNameClash` (names type and qualified method id): the schema of that tenant is not built.
- **Arguments**: instance methods `rtId: OctoObjectId!`; static none. `input: {Declaring}{Method}MethodInput`, built with `OctoValueTypeMapper` (the attribute mapping),
  required parameter = non-null; the argument is non-null only if a parameter is required, absent without parameters. **Every** method field also takes the optional
  `idempotencyKey: String` (see `docs/method-idempotency-audit.md`). **Result** `{Declaring}{Method}MethodResult`:
  `invocationId: ID!` and `result` (declared value type) - no error/success member. The field itself is nullable.
- **Marker**: the field metadata key `Statics.CkMethodCall` (`"CkMethodCall"`) holds a `CkMethodCallInfo` (concrete type, method id, qualified id, kind); `Statics.CkId` holds the
  type. The gateway rules (one call per request) find a method call by this key, never by field name.
- **Errors**: `CkMethodResponseMapper.Map` returns the typed value for a success; for every failed response ONE `ExecutionError` via `CkMethodGraphQlError.Create` (`extensions.code`,
  `extensions.parameters`, plus `extensions.details` when the response carries details). Declared handler codes keep their own code. An asynchronous method (`execution.mode: async`)
  maps an accepted response to `invocationId` + `operation` (the handle, next section); a sync method that was nevertheless accepted maps to the
  invocation id without result (unchanged). A result that does not fit its declaration (`ResultJson`) becomes `METHOD_HANDLER_FAILED` with `methodId` and `correlationId`, never the value.
  The result JSON is the camelCase `CkMethodJson` wire form; enums may be the member name (any spelling) or the key; records are built as `RtRecord` from the record graph.
  **`ResultJson` is the bare value:** the handler host of octo-common-services writes only `Value` of the generated `{Type}{Method}Result` (`"6ae7…"`, `21`,
  `{"specName":...}`), never `{"value": ...}`. The converter deliberately has **no** wrapper tolerance: a String result whose JSON is not a string is still passed
  through as its raw text (unchanged), so an older handler host keeps answering exactly as before during a rolling update instead of failing a call whose writes already
  happened. Pinned end to end (real in-process host + this mapper, String/Int/Boolean/Record) by `UnitTests/GraphQL/Methods/CkMethodResultEndToEndTests`; it needs the
  octo-common-services package whose handler host answers with the bare value.
- **Resolver**: `CkMethodFieldResolver` goes through the gateway (next section).
- **Schema build time**: the log line carries the CK method count; see the numbers in the PR that added the typed mutations (`SchemaBuildTimingTests`, 11 runs, median, with the 50-method type
  `MethodLoadThing` in the test model).
- Tests: `CkMethodMutationFieldsTests`, `CkMethodResponseMapperTests` (unit); `GraphQL/CkV2/TypedMethodMutationTests` (integration: introspection of instance/static/versioned/inherited
  methods, input and result types, marker, 50 methods, snapshot `GraphQL/Methods/MethodLessTypes.golden.graphql` of the mutation and input types of method-less types taken before this
  change, call answer). Test model: `types/methodTest.yaml`, `types/methodLoadTest.yaml`, `interfaces/operable.yaml`.

## Asynchronous methods: operation handle and `runtime.operations`

A method declared `execution.mode: async` (read from the tenant's CK cache; `CkMethodGraphTypes.IsAsync`) gets a result type with
`invocationId` and `operation: CkMethodOperationHandle!` and **no** `result` field; sync methods keep their exact shape. The handle type has the four fields of the frozen wire
contract (`operationId`, `acceptedAtUtc`, `retryAfterSeconds`, `expiresAtUtc`, `CkMethodOperationHandle` of common-services). `CkMethodResponseMapper` passes the
envelope's handle through; an async method answered **without** a handle is `METHOD_HANDLER_FAILED` with `correlationId` (logged, no value leaves).

`runtime.operations` (`GraphQL/Methods/CkMethodOperationQueryField`, added to `RuntimeModelQuery` on **every** tenant) reads the operation records of asynchronous methods through
`ICkMethodOperationReader` (`CkMethodOperationReader`) and the store contract `ICkMethodOperationStore` (no store of its own). Arguments: `operationId` (one record,
or `OPERATION_NOT_FOUND`), `methodId`, `state`, `since` (also narrow a read by id), `allOwners` (default false), `skip` (default 0), `take` (1-200, default 50).
Result `CkMethodOperationConnection { totalCount items }`; item `CkMethodOperation` (`operationId`, `methodId`, `invocationId`, `state`
`ACCEPTED|RUNNING|SUCCEEDED|FAILED`, `progress { percent stageCode parameters { name value } }`, `result: SimpleScalar` (parsed `ResultJson`, null for a sensitive
result because the record never holds it), `error { code parameters details }`, `acceptedAtUtc`, `startedAtUtc`, `completedAtUtc`, `expiresAtUtc`). The field is nullable:
an error does not null the whole `runtime` object.

- **Who reads**: within the route tenant only, the owner (`CkMethodOperationOwner.Of(CkMethodPrincipal.FromClaimsPrincipal(user))`, the same mapping the gateway uses for
  the call: `sub`, or `client_id` of a token without `sub`) and tenant administrators (`Methods:Authorization:AdministratorRoles`, default `AdminPanelManagement`, the
  setting of the method authorization). Another user, an unknown, expired, other-tenant, empty or over-long (> 128) id: the **same** `OPERATION_NOT_FOUND` (fixed message,
  empty `parameters`). The list without `allOwners` is the caller's own operations (also for administrators); `allOwners: true` for a non-administrator and invalid paging
  are `OPERATION_QUERY_REFUSED` (`reason` `administrators-only` / `invalid-paging`, `argument`). Errors are ONE `ExecutionError` with `extensions.code` + `extensions.parameters`
  (`CkMethodOperationQueryField.CreateError`), GraphQL-layer codes, not `METHOD_*` wire codes.
- **Registration** (`AddServices`): `AddCkMethodMongoOperationStore()` (package `Meshmakers.Octo.Services.Methods.MongoDb`, collection `MethodOperations` in the system
  database) and `AddCkMethodOperationReads()` (reader + the sweep that ends lost operations; no heartbeat, the asset repository runs no operation itself; a
  gateway-owned async handler would additionally need `AddCkMethodOperations()`). Settings `Methods:Operations:*` (retention 1 h-30 d, default 7 d).
- **Idempotency**: a repeated key of an async method returns the stored accepted envelope, so the same `operationId` (dispatch layer; proven end to end here).
- **One call per request**: an async method field still counts (`OneMethodCallPerRequestRule`).
- Tests: `UnitTests/GraphQL/Methods/CkMethodOperationReaderTests` (access matrix, not-found identity, list filters, paging, configured admin roles, client-credentials owner),
  `CkMethodResponseMapperTests` (handle passthrough, async without handle); `IntegrationTests/GraphQL/CkV2/AsyncMethodOperationTests` (fixture
  `AsyncMethodGraphQlTestFixture`: real schema, gateway, in-process handler host with `AddCkMethodOperations`, the **MongoDB** operation store of the production
  registration; handle fields, owner/admin read, progress, declared failure, not-found identity incl. expired and other-tenant ids, a read through another tenant's route,
  list filters and `allOwners`, same idempotency key = same `operationId`, one call per request, schema of the async result type). Test model: `types/asyncOperationTest.yaml`
  (`AsyncOperationThing.Export-1`, static, async, String result).
- **Studio codegen**: the new types and the `operations` field change `schema.graphql`; a refresh is needed only to use them.

## Parent-tenant administration of CK methods

The method counterpart of the REST jobs endpoint's `[AllowParentTenantAdministration]` (bot-services): an administrator of a
parent tenant P backs up, restores and exports/imports the archive data of a child tenant C through the CK methods and follows the
operations through `runtime.operations`. **Mirror of the REST rule, no wider access.** The rule itself lives in octo-common-services
(`CkParentTenantAdministrationMethods`, `CkMethodCallerTenantRule.EvaluateAsync`, `octo-common-services/docs/method-pipeline.md`); this service has three
pieces:

- **Gateway** (`CkMethodGateway`, step 0): `CkMethodCallerTenantCheck.CheckAsync` with the registered `ITenantHierarchyReader`. A **user**
  token of P passes for a method of the **closed list** (`System.Bot/TenantRepository.Dump-1`, `Restore-1`, `ExportArchiveData-1`,
  `ImportArchiveData-1`) when C is a registered child of P (one level; the system tenant reaches every tenant; cached, fail-closed).
  Every other method, a sibling, a grandchild, a child calling its parent, a deleted or unregistered child and every service token
  stay `METHOD_FORBIDDEN` / `tenant-not-allowed`, nothing read or dispatched. The declared roles (`TenantManagement`, the REST policy
  `TenantJobManagementPolicy`) are judged on P's token as for every caller. The bus host in bot-services re-checks the forwarded token
  with the same rule (`ValidateForMethodAsync`) including the tenant generation of P.
- **`runtime.operations`** (`CkMethodOperationReader`): a user token issued for another tenant than the route reads only on the
  parent path: role `TenantManagement` **and** a registered child; then only records of the closed list
  (`CkMethodOperationQuery.MethodIds`, and the page is filtered again in the reader in case a store ignores the set), with the
  owner/administrator rule (`allOwners` needs an administrator role of P); a parent read is logged at Debug (clients poll). Otherwise a
  read by id is `OPERATION_NOT_FOUND` and a list is empty. **Narrower than before:** earlier the reader did not look at the token
  tenant at all (it relied on the HTTP gate, which is `LogOnly` in this service), so a foreign user token read its own records in
  another tenant; now it reads nothing there unless it is a parent administrator. Service tokens and own-tenant tokens are unchanged.
  The result of a dump carries `jobId` and `downloadPath`; the download is the bot-services jobs route of C, which already carries
  the parent rule and `IJobTenantAccessGuard`.
- **HTTP gate predicate** (`GraphQL/Methods/GraphQlParentTenantAdministrationRequest`, mapped on `tenants/{tenantId}/graphQl` with
  `.WithMetadata(...)` in `OctoApplicationBuilderExtensions`): implements `IAllowParentTenantAdministrationRequest` of
  octo-common-services, **never** the endpoint-wide `IAllowParentTenantAdministration` (pinned by `ProgramTenantGatePinTests`).
  `TenantAuthorizationMiddleware` asks it only for a user token of another tenant and grants the request only when it answers `true`
  and C is a registered child of P; in `LogOnly` such a grant is not logged as "would be denied". It answers `true` only for a single
  `POST` with `application/json` (no batch array, no query string, no other body type, only `query`/`variables`/`operationName`/empty
  `extensions`, each once and in exactly this spelling, at most 64 KiB) whose every operation is either `mutation { runtime { systemBotTenantRepositorys { dump|restore|exportArchiveData|importArchiveData } } }`
  or `query { runtime { operations } }`, judged by field **name** (an alias cannot disguise a field), with no fragment spread, inline
  fragment, fragment definition, introspection or subscription down to that level. A request that mixes an allowed and a forbidden
  field anywhere (also in a second operation of the document) is refused as a whole. The field names come from the same naming
  functions as the schema (`RtMutation`, `CkMethodMutationFields.FieldName`). **Narrower than the AC allows:** batches, fragments and
  GET are refused even when every element would be allowed; a client must send one plain operation per request.
- Tests: `UnitTests/GraphQL/Methods/ParentTenantAdministrationGatewayTests`, `CkMethodOperationReaderParentTenantTests`,
  `GraphQlParentTenantAdministrationRequestTests`, `Configuration/ProgramTenantGatePinTests`. The REST-vs-method parity matrix is in
  octo-common-services (`ParentTenantAdministrationParityTests`).

## Method mutation resolver

`GraphQL/Methods/CkMethodFieldResolver` is the resolver of every generated method field. It does no authorization or loading itself: it builds a
`CkMethodGatewayCall` and lets `ICkMethodGateway` decide. **Order of checks (owned by the gateway):** authenticate (no authenticated principal = no
principal, default-deny) -> **caller tenant** (a token issued for another tenant, or a user token without `tenant_id`, is
`METHOD_FORBIDDEN` / `tenant-not-allowed` for every call, before anything below, so it never learns whether the method or the target
exists; `CkMethodCallerTenantCheck` of common-services, the rule incl. `TenantAuthorization:CrossTenantServiceClientIds`,
independent of the HTTP gate's mode; tests `CkMethodGatewayAdversarialTests.C02c/C02d`,
`ArchiveLifecycleMethodTests.ATokenOfAnotherTenant_LearnsNothing...`) -> authorize (roles any-of, `allowSelf` on the target id **as the client sent it**, declared scopes, `octo_api` always,
default-deny) -> **parameter size** (`METHOD_PARAMETERS_TOO_LARGE`, `Methods:MaxParametersBytes`, default 1 MiB, UTF-8 bytes of the parameter JSON, exactly the limit passes; `CkMethodParameterLimit` of common-services) -> load the target and check the data permission (`CkMethodTargetAccessCheck` callback) -> validate the parameters -> dispatch -> map the envelope. The mapper (`CkMethodResponseMapper`) applies the host's allow-list: platform catalog codes and codes the method declares pass, any other code (also an unknown `METHOD_` code) becomes `METHOD_HANDLER_FAILED` with the correlation id (invocation id) and the original code is logged, never returned.

- **Same answer whether the target exists or not.** A caller who fails the authorization gets `METHOD_FORBIDDEN` (code, message, `parameters`, `errors[].extensions.code`
  identical) for an existing and a missing target, because nothing about the target is read before the check. Only an authorized caller can get
  `METHOD_TARGET_NOT_FOUND` (`methodId` as parameter). `allowSelf` compares the ids case-insensitively.
- **Call**: tenant, concrete CK type (`RtCkId.FullName`), qualified method id, `rtId.ToString()` (instance), parameters JSON, the request principal
  (`GraphQlUserContext.User`), the raw bearer token (read from the `Authorization` header via `IHttpContextAccessor`; forwarded to the handler, never logged)
  and the `CkMethodTargetAccessCheck` callback (instance and static). `CkMethodParameterJson` turns the parsed `input` dictionary into the wire form of `CkMethodJson` (enum as key,
  dates ISO 8601, time spans in the `c` format, binaries base64, records as objects of their attributes).
- **Target lookup is caller-scoped and outside the request transaction**: `repository.GetSession(Helpers.GetSecurityContext(userContext))` is a plain read in a
  session of its own, never part of the request's Mongo transaction (methods run outside it, decision 2026-10-07). The caller's `RtSecurityContext` applies, so
  data permissions hide a record from an authorized caller as "not found"; the data-permission check proper is described in `docs/method-in-process.md`.
- **Parameter validation** (`CkMethodParameterValidator`, in the gateway between the data-permission check and dispatch, so it runs only for an authorized caller and a visible, permitted target):
  `METHOD_VALIDATION_FAILED` with `methodId`, `field` (offending members, sorted, comma separated) and `reason` (distinct fixed reasons `missing-required`, `unknown-member`,
  `wrong-type`, sorted, comma separated). No value ever appears in the error or a log line. Nothing is dispatched. GraphQL already rejects most type errors of the typed `input`
  before the resolver runs; this check also protects the gateway for every other caller.
- **Response mapping**: `CkMethodResponseMapper.Map` -> the typed result (`invocationId`, `result`) or ONE error (declared handler errors, `METHOD_HANDLER_NOT_AVAILABLE`,
  `METHOD_TIMEOUT`, `METHOD_HANDLER_FAILED` alike) and `data.<field>` = null. An exception is mapped through `HandleException`.
- **Logging**: the resolver logs nothing; the gateway logs method id, tenant, invocation id and the rule/reason at Information, never target id, parameters or token.
- **Scope**: a token without `octo_api` is refused for a method mutation (`METHOD_FORBIDDEN`) while the generic mutations keep following `GraphQl:EnforceMutationScope`.
- **Registration** (`RuntimeEngineBuilderExtensions.AddServices`): `AddCkMethodDispatcher()`, `AddCkMethodAuthorization()`, `AddCkMethodGateway()` (which also adds
  `AddHttpContextAccessor()` and the in-process session tripwire) and `AddCkMethodHandlerRegistryFeed()` in the event-hub configuration, so the dispatcher learns the live handlers from
  their announcements. These options bind from configuration; a host without `IConfiguration` (integration-test service collections) gets an empty one from `ServiceCollectionFixture`.
- Tests: `CkMethodGatewayTests` (validation order, no value in error or log), `CkMethodParameterValidatorTests`, `CkMethodParameterJsonTests` (unit);
  `GraphQL/CkV2/MethodMutationGatewayTests` (integration, fixture `MethodGatewayGraphQlTestFixture`: real schema, gateway and MongoDB, scripted dispatcher and log capture: forbidden is identical
  for an existing and a missing target, target not found, `allowSelf` across letter case, typed result, static method, each failure code, missing `octo_api`, no secret/target id in logs or errors).
