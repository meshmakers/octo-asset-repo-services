---
description: Method data permissions, in-process handlers and the session tripwire, gateway-owned methods, adversarial suite, canary scan and replaced generic create.
applies_to: **/GatewayOwnedMethodHandlers.cs, **/GenericCreateReplacedBy*.cs, **/OctoSessionAmbientStateProbe.cs, tests/*MethodCanaryScan/**
---

# CK methods: data permissions, in-process handlers and adversarial checks

## Method data permissions

A method call never touches a record that data permissions hide or protect from the caller. Order: authenticate -> declared authorization (a pure function of method
and caller: `METHOD_FORBIDDEN`, `not-authorized`, identical for an existing and a missing target) -> **load the target + data-permission check** -> parameter validation -> dispatch.
Code: `GraphQL/Methods/CkMethodTargetAccess.cs` (the delegate `CkMethodTargetAccessCheck(CkMethodDataAccessDto, ct)`, its result `CkMethodTargetAccess` with `Allowed`/`NotVisible`/`NotPermitted`
plus the audit flag, and the error `CkMethodDataPermissionErrors`), `CkMethodTargetAccessChecker` (the production check), gateway step 2 in `CkMethodGateway`.

- **Parameter is required.** `ICkMethodGateway.InvokeAsync(call, targetAccess, ct)` takes a non-null check for instance **and** static methods; there is no way to skip the data-permission check.
  A fault in the check (exception) fails the call, it never lets it pass.
- **Caller-scoped load.** `CkMethodTargetAccessChecker.ForInstance` loads the target in a session of its own bound to the caller's `RtSecurityContext` (`Helpers.GetSecurityContext`), outside the request
  transaction. No result = `NotVisible` = `METHOD_TARGET_NOT_FOUND` with `methodId` only, byte-identical to a missing target. It then asks the engine's public check
  `IRtDataPermissionEvaluator.EvaluateAsync` (the rules the repository enforces) for the data action on the **stored** type of the loaded entity (not the type of the field: a derived type may be
  protected by its own policy). `NotVisible` -> not found; `NotPermitted` -> `METHOD_FORBIDDEN` with `methodId` and the fixed reason `data-permission`; `Allowed` -> go on. An unknown decision refuses.
- **Data action** = `CkMethodAuthorization.Resolve(definition.Authorization)`: `Write` by default, `Read` if the method declares `authorization.dataAccess: Read` (the declaration only reaches a
  tenant's CK cache once engine-mongodb persists it, until then every method is `Write`, the stricter default).
- **`allowSelf` does not exempt the check.** A user whose role has no grant on the record is refused on their own record; the model must grant it (e.g. OwnedOnly).
- **Static methods** (no target): `EvaluateTypeAsync` on the declaring type only. A type the caller cannot read at all is `METHOD_FORBIDDEN` (there is no target to hide), not "not found".
- **Audit-only policy**: the call passes; the engine publishes its audit event (`publishAudit: true`) and the gateway logs one Information line (method id, invocation id, tenant, action).
- **No leaks**: refusal log lines name method id, invocation id, tenant and action, never the target id, parameters or token; error texts are fixed.
- Handlers run with service rights (option A, the current state); the caller-bound handler session is the later end state and not built yet.
- Tests: `CkMethodGatewayTests` (unit: hidden = missing, no write grant, data action write/read, `allowSelf`, static, audit-only, fault, logs, check never called for a role refusal) and
  `GraphQL/CkV2/MethodDataPermissionGatewayTests` (integration, fixture `MethodDataPermissionGraphQlTestFixture`: policies on `AccessTestAccount` through the `IDataPermissionResolver` seam, real
  MongoDB and engine; the generic mutation gives the same verdict). The `Read`-declared case runs against a real tenant only after engine-mongodb persists `dataAccess` (unit-tested until then).

## In-process handlers never see the request session

A CK method handler that the asset repository runs **in-process** (`AddCkInProcessMethodHandlers`, `ICkInProcessMethodTransport`) runs outside the
request's Mongo transaction (decision 2026-10-07). Guarantee: **`IOctoSessionAccessor.HasSession` is `false` inside the handler**, also when the
call is started from a GraphQL mutation that holds the request session, and the handler can neither commit nor abort that session. Two layers
(octo-common-services >= 3.5.34): the transport starts the handler under a suppressed `ExecutionContext` in a new DI scope per call, so the static
`AsyncLocal` of `OctoSessionAccessor` is empty there; and the tripwire `GraphQL/RequestHandling/OctoSessionAmbientStateProbe`
(`ICkInProcessAmbientStateProbe`, backed by `HasSession`, resolved from the handler's new scope) is asked at handler entry, before the handler is created. A
probe that reports `true` (a regression) or throws (e.g. no accessor registered; fail closed) fails the call with `METHOD_HANDLER_FAILED`
(`methodId`, `correlationId`) and the handler body does not run. Registration: `AddCkMethodGateway()` calls `AddOctoSessionAmbientStateProbe()`
(idempotent), so every host that wires the gateway also gets the tripwire.
Tests: `UnitTests/GraphQL/Methods/InProcessSessionIsolationTests` (real `OctoSessionAccessor` and transport; HasSession false in the handler, request
session still usable and committable, handler cannot end it, request-scoped repository is a new instance, tripwire with a test accessor that lets the
session flow, fail closed, registration once) and `IntegrationTests/GraphQL/Mutations/InProcessMethodSessionIsolationTests` (a real mutation inside
the real `OctoSessionListener` transaction; harness `InProcessMethodGraphQlTestFixture` starts the method from a document listener registered after
`OctoSessionListener`, i.e. inside the session and the transaction of the mutation).

## Gateway-owned methods run as in-process handlers

A method the asset repository implements itself (`handler: service`, e.g. the planned `Tenant.*` / `Archive.*`) is registered in
`GraphQL/Methods/GatewayOwnedMethodHandlers.Register` (empty today; one line per handler, version-less qualified method id; the `Archive.*` methods of
System.StreamData register through `StreamData/Methods/ArchiveMethodHost` instead, see `docs/streamdata-graphql.md`). `AddGatewayOwnedCkMethodHandlers()`
(`RuntimeEngineBuilderExtensions.AddServices`, after `AddCkMethodGateway()`) hands the list to `AddCkInProcessMethodHandlers`
(octo-common-services >= 3.5.48, which contains the host-side authorization and the method limits) and adds the session tripwire once.
The dispatcher prefers the in-process transport, so such a method is never published to the bus and never announced as a remote handler; success, declared error,
`METHOD_HANDLER_FAILED` and `METHOD_TIMEOUT` have the envelope of the bus path. **No bypass**: the transport runs the same invocation pipeline and the host evaluates the declared
`authorization` before the handler is created; the principal comes from the gateway (`CkMethodPrincipalScope`), a call without one is `METHOD_FORBIDDEN`. `AddCkMethodCallerTokenValidation()`
is deliberately not registered here: it validates the token of a call that arrives over the bus at a remote handler host, and the asset repository hosts none. `METHOD_FORBIDDEN` from an in-process call has three causes, check them in this order: the call has no principal (not started through the gateway, no `CkMethodPrincipalScope`), the principal lacks the
role/scope the method declares, or the definition is missing from the CK cache or declares no admitting role/scope (fixtures: `MethodCkCacheFake`, `InProcessMethodGraphQlTestFixture`). Guide with the sample: `docs/gateway-owned-method-handlers.md`.
Tests: `IntegrationTests/GraphQL/CkV2/GatewayOwnedMethodInProcessTests` (fixture `GatewayOwnedMethodGraphQlTestFixture`: real schema, gateway, dispatcher and authorization, bus double asserted untouched,
the four envelopes compared with the remote handler host of the same handler class, no-bypass cases) `GatewayOwnedMethodProductionRegistrationTests` (the production `AddServices` setup registers the transport and no remote host) and `UnitTests/GraphQL/Methods/GatewayOwnedMethodHandlersTests`.

## Adversarial suite for the method path

Attack tests for the asset-repo half of the method path; the bus half, the case table, the findings (F-1 .. F-3) and the accepted
limitations are in `octo-common-services/docs/method-adversarial-suite.md`. Each run writes `adversarial-results.md` next to the
test assembly. Tokens are generated per run (throw-away RSA key, fake identity service); no shared stack.

- `tests/AssetRepositoryServices.UnitTests/Adversarial/BearerTokenAttackTests` - the **real** `ConfigureJwtBearerOptions`, the real
  tenant gate (`Enforce`) and the real method authorization evaluator behind a minimal endpoint: other tenant, expired, wrong
  signature, `alg: none`, key confusion, other issuer, no `octo_api`, missing role. Pins two limitations: another audience is accepted
  (L-2) and the `LogOnly` code default lets a foreign-tenant token through (L-3).
- `.../Adversarial/CkMethodGatewayAdversarialTests` - the real gateway with a counting target access: unauthenticated, allowSelf with
  another id (also upper case), default-deny, identical refusals, malformed parameters, canary over logs and errors; the parameter size
  limit and the responder code allow-list of the GraphQL error are enforced and tested.
- `tests/AssetRepositoryServices.IntegrationTests/GraphQL/CkV2/MethodAdversarialGraphQlTests` - real schema and MongoDB: a `caller`
  member in the mutation input, a client-chosen deadline, no user.

Rerun: `dotnet test tests/AssetRepositoryServices.UnitTests -c DebugL --filter "FullyQualifiedName~Adversarial"` and
`dotnet test tests/AssetRepositoryServices.IntegrationTests -c DebugL --filter "FullyQualifiedName~MethodAdversarialGraphQlTests"`
(Docker needed for the second). A closed gap turns its `_Gap_`/`_Limitation_` pin red; enable the skipped test next to it.

## Method canary scan on the local stack

`tests/AssetRepositoryServices.MethodCanaryScan` proves on the **running** services that a sensitive method parameter and the caller's token reach
no GraphQL answer, no service log file (`logFiles/*.log`), no event of the tenant's audit log and no exported trace/metric/log (local OpenTelemetry
collector with the file exporter, services started with the unprefixed `OTLP_ENDPOINT_URL` - asset-repo and identity read it before
the `OCTO_` provider is added - and identity with `OCTO_METHODS__IDENTITY__ENABLEPASSWORDMETHODS=true`; the services export traces and
metrics, no logs, over OTLP). Two tests: `MethodCanaryScan/CanaryAccount.Rotate-1` (test model
`tests/MethodCanaryScanCkModel`, `OctoPublishCkModel=false`, handled by the scan's own bus handler host: success, handler failure, timeout, validation,
declared error, refused) and `System.Identity/User.CreateUser-1` (hosted by identity). Canaries in every serializer form, the scan's tokens
and client secrets; every case also asserts its outcome. Not in CI: the project is not named `*Tests` and skips unless `OCTO_METHOD_CANARY_SCAN=1`.
Set-up, command and variables: the project's `README.md`; user docs: octo-documentation `ckLanguage2/methodHandlers#canary-scan`. A hit is a Bug.

## Generic create refused for types whose method replaces it

A static CK method with `replacesGenericCreate: true` (engine/engine-mongodb >= 3.5.34) means entities of its declaring type **and of every
type derived from it** are created only through that method. All three create paths - the per-type `create` of `RtEntityMutation`, the untyped
`runtimeEntities.create` of `RtEntityMutationGeneric` and the query-row `runtimeQuery(rtId).create` of `RtQueryMutation` (the row `ckTypeId` is not
validated against the query's type, so every distinct row type is checked before the query mapper runs) - call `GraphQL/Methods/GenericCreateReplacedByMethodGraphQlError.TryCreate` first (before any
entity is built, so nothing is written). The lookup is the shared engine helper `GenericCreateReplacement.TryGetReplacingMethod` (`ConstructionKit.Contracts.DependencyGraph`; MCP and blueprint apply use the same one - no private copy here). It reads the type by its **version-less** `RtCkId` (`ICkCacheService.TryGetRtCkType`; never a generated
versioned id) and looks for a `CkMethodGraph` with `ReplacesGenericCreate` in `CkTypeGraph.AllMethods`, which already includes the inherited methods
(the compiler allows at most one such method per concrete type, inherited ones included). The answer is ONE GraphQL error, `data.<field>` = null:
`extensions.code` = `GENERIC_CREATE_REPLACED_BY_METHOD` (`Statics.GraphQlGenericCreateReplacedByMethod`, a GraphQL-layer code, not a code of the method
response envelope) and `extensions.parameters` = `{ ckTypeId, methodId }` (`methodId` = qualified id `Model/Type.Method-1` of the declaring type). The
message is fixed, contains only ids, and carries no entity data, so it is byte-identical for every caller. It needs no method right and runs after
authentication like any mutation. Update, delete, query-row update/delete and every other mutation are untouched; types without the flag are untouched.
The CK meta field `CkMethod.replacesGenericCreate` (`Boolean!`, additive) shows the flag; v1 CK queries are unchanged.
**`System.Identity/User`:** System.Identity 3.0.0 sets `replacesGenericCreate` on the static `User.CreateUser-1` (until 2.30.0 the engine
helper stood in with a runtime rule), so the typed, generic and query-row create of a **new** user are refused here with
`methodId = System.Identity/User.CreateUser-1`; updates of existing users are untouched. No code in this repository changed for it (engine package only);
the CK meta reports `replacesGenericCreate: true` for the method. Tests: `Security/IdentityCredentialGuardGraphQlTests` (`GenericCreate_OfAUserWithOnlyVisibleFields_*`,
`TypedCreate_OfAUser_*`, the create cases), `Security/IdentityCredentialGuardBlueprintSeedTests` (test blueprint `NewUserSeedBp`, System.Identity 3.0: new user seed refused with 136,
seed of an existing user rtId upserted; `VisibleIdentitySeedBp` (visible attributes only) is refused with 136 too since 3.0.0; `MethodOnlySeedBp` with 139), `Security/IdentityCredentialGuardExchangeTests`
(caller `ImportRt` of a new user: 137).
**Create paths that do not go through the GraphQL create and are therefore not covered here:** `ModelsController.ImportRt` (async `ImportRtCommandRequest`
job), blueprint seed data and `ImportCk`-time data, the mesh-adapter `ApplyChanges` nodes (repository access), and `FileSystemService`/system
types (never flagged). There is no REST endpoint that creates arbitrary runtime entities. Tests: `UnitTests/GraphQL/Methods/GenericCreateReplacedByMethodGraphQlErrorTests`,
`IntegrationTests/GraphQL/CkV2/GenericCreateReplacedByMethodTests` (incl. the query-row create) (test types `ManagedThing`, `ManagedThingChild`, `ToolThing` in
`tests/AssetRepositoryIntegrationTestCkModel/ConstructionKit/types/replacesGenericCreateTest.yaml`).
