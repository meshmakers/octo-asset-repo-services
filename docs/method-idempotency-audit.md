---
description: Method idempotency store and key argument, audit of gateway refusals and faults, and the methodHandlers diagnostics query.
applies_to: **/Services/Methods/**, **/GraphQL/MethodHandlers/**, **/GraphQL/MethodHandlersQuery.cs, **/CkV2/MethodIdempotencyKeyTests.cs
---

# CK methods: idempotency, audit and diagnostics

## Method idempotency store (AB#7824, F3.5)

`Services/Methods/MongoMethodIdempotencyStore.cs` implements `ICkMethodIdempotencyStore` (octo-common-services, contract AB#7817) for
the band-300 `CkMethodIdempotencyFilter`; registered in `RuntimeEngineBuilderExtensions` next to `AddCkMethodIdempotency()`.
Collection `MethodIdempotency` in the **tenant database** (included in tenant backup, removed with the tenant); atomic claim = insert
with unique `_id` (SHA-256 key id; the raw key and raw parameters are never stored, the fingerprint is stored as given); expired records
are taken over with a conditional `findOneAndReplace`; TTL index `expiresAt_ttl` (`expireAfterSeconds: 0`) is housekeeping only, readers
compare the per-document `expiresAt` themselves (lifetime `Methods:Idempotency:Ttl`, default 24 h, max 7 d, applies to new claims).
**Fails closed**: every database problem is a `CkMethodIdempotencyStoreException` (unlike `MongoFileSystemNameLeaseStore`, no admin
credentials is a refusal, not a pass). `Complete`/`Release` do not carry the tenant, so the store remembers the tenant of its own
in-flight claims. Keyed calls need `Methods:Idempotency:Fingerprint:Current` (Id + base64 secret >= 32 bytes). The `idempotencyKey`
GraphQL argument is AB#7825 (next section). Tests: `IntegrationTests/Methods/MongoMethodIdempotencyStoreTests` (private MongoDB; race, takeover,
expiry, fail closed, stored document, benchmark). Details and latency: `docs/method-idempotency-store.md`.

## Method idempotency key argument (AB#7825, F3.5)

Every typed method mutation field takes the optional argument `idempotencyKey: String` (`CkMethodMutationFields`, `Statics.IdempotencyKeyArg`; instance and
static methods alike). `CkMethodFieldResolver` puts it into `CkMethodGatewayCall.IdempotencyKey`, the gateway copies it unchanged into
`CkMethodRequest.IdempotencyKey`, and the band-300 `CkMethodIdempotencyFilter` of octo-common-services applies the OD-4 rules (AB#6922); the gateway and the resolver
validate nothing about the key themselves (the filter does, after authorization, the data-permission check and the parameter validation of the gateway). Rules as seen by
a client: scope = tenant + method + caller + key (caller = user `sub`, or `client:<client_id>` for a client-credentials token without `sub`: the gateway maps `client_id` through `CkMethodPrincipal.FromClaimsPrincipal`, AB#8078; tests `MethodIdempotencyKeyTests`, `CkMethodGatewayTests`; max 128 characters, longer = `METHOD_VALIDATION_FAILED` with `field = idempotencyKey`, `reason = key-too-long`);
same key with other parameters = `METHOD_VALIDATION_FAILED` / `parameters-changed`; first call still running = `METHOD_REQUEST_IN_PROGRESS`; unknown outcome =
`METHOD_OUTCOME_UNKNOWN` (the key is burned, as after a failed handler); a replay returns the first response with the new call's `invocationId` (the first invocation id is
only in the audit facts as `ReplayOf`); a `resultSensitive` method refuses a key (`result-sensitive`); no fingerprint key configured = `fingerprint-key-missing` /
`fingerprint-unavailable`; store down = `METHOD_HANDLER_NOT_AVAILABLE` with `reason = idempotency-store-unavailable`. All codes are platform codes, so
`CkMethodResponseMapper` passes them through `CkMethodGraphQlError` (`extensions.code`, `extensions.parameters`); neither the key nor a parameter value is in an error.
Without the argument nothing changes (the filter passes an unkeyed call on without touching the store). The argument is additive: a replica without this change rejects it at
schema validation (fail closed, a key is never silently ignored). There is no generic (non-typed) method invocation in the asset repository; the typed mutations are the only
GraphQL entry. After a schema change, Studio refreshes `schema.graphql` and runs codegen. Tests:
`CkMethodMutationFieldsTests` (argument on every field), `CkMethodGatewayTests` (forwarding), `CkMethodResponseMapperTests` (codes), `IntegrationTests/GraphQL/CkV2/MethodIdempotencyKeyTests`
(real schema, gateway, pipeline and in-process handler with `IdempotentMethodGraphQlTestFixture`: replay runs the handler once, other key / other caller run again, changed
parameters, 129 characters, 128 accepted, forbidden stays forbidden, failed handler burns the key).

## Gateway refusals and faults are audited (AB#7826, AB#7907, F3.5)

A call that the gateway refuses never enters the invocation pipeline, so the band-100 audit filter of octo-common-services (AB#6919) cannot see it.
`CkMethodGateway.RefuseAsync` therefore records every pre-dispatch refusal through the same `ICkMethodAuditRecorder`: `METHOD_FORBIDDEN` (declared authorization,
also an unknown method or an unauthenticated caller, and the data permission), `METHOD_PARAMETERS_TOO_LARGE`, `METHOD_TARGET_NOT_FOUND` and `METHOD_VALIDATION_FAILED`. The record is
built in the gateway: transport `gateway` (`CkMethodAuditRecord.GatewayTransport`), the invocation id the gateway minted (the one in the response), outcome = the error code of the
answer, no handler instance, caller subject (`sub`, else `client_id`) and `name` claim, method id, CK type, target id as the client sent it, duration. The record type has no place for
parameters, the idempotency key or the token, so none can reach the event.

- **Exactly one event per invocation id.** A call that passes the gateway is recorded by the filter only (transport `in-process` or `bus`); the gateway writes nothing for it.
  The idempotency refusals of AB#7825 (`idempotency key too long`, `parameters-changed`, `METHOD_REQUEST_IN_PROGRESS`, `fingerprint-unavailable`, store unavailable ...) are answered by the
  band-300 filter **inside** the pipeline, so the audit filter already records them (transport `in-process`/`bus`); the gateway does not answer any idempotency code itself.
- **Registration.** `AddCkMethodGateway()` calls `AddCkMethodAudit()` (recorder + audit filter, safe to repeat). The sink is the event log after `AddOctoNotification()`.
- **A failed audit write never turns a refusal into something else.** The recorder throws `CkMethodAuditFailedException` only when the tenant is configured to fail calls on audit failure
  (`Methods:Audit:FailCallOnAuditFailure`); a refused call did not run, so the gateway keeps the refusal (the recorder has logged and counted the failure, meter
  `Meshmakers.Octo.Methods`, `ck.method.audit.failures`). Any other recorder exception is logged and swallowed. The write uses `CancellationToken.None`, like the filter.
- **Faults are audited too (AB#7907).** `CkMethodGateway.InvokeAsync` wraps the whole gateway flow (`InvokeCoreAsync`: authorization, size limit, target-access check, validation, dispatch)
  and records an exception that escapes it once, through the same recorder: transport `gateway`, the minted invocation id, no handler instance, outcome `METHOD_HANDLER_FAILED`, then rethrows
  (`throw;`), so the caller gets exactly the error it got before (the resolver's `HandleException`). A cancellation by the caller (`OperationCanceledException` with the caller's token cancelled)
  is no fault; AB#7930 records it separately (next bullet). The log line names only the exception **type** (the exception text of a store may carry values); the event has no place for exception text.
  **Outcome decision:** `METHOD_HANDLER_FAILED`, no new gateway fault code. Reasons: (1) it is the code the band-100 filter already writes for an exception inside the pipeline, so one query finds all
  unexpected platform failures; (2) `CkMethodMetrics.OutcomeLabel` and the audit level (`Error`) only know the catalogued codes, an invented outcome would be counted as `invalid_code` and logged
  as `Warning`; (3) a new code is a wire/contract change in octo-common-services; (4) the combination transport `gateway` + no handler instance + this code says "failed before a handler was reached".
  **Counted once:** `CkMethodAuditRecorder.RecordAsync` counts `ck.method.calls` for transport `gateway` (AB#6920), so the fault is counted exactly once with no change in common-services
  (test `AFault_IsCountedOnce_AsAGatewayCall_WithOutcomeHandlerFailed`).
  **No duplicates:** the real `CkMethodDispatcher` and the pipeline never throw for a failing call (the filter records an exception from an inner step as `METHOD_HANDLER_FAILED`, rethrows to the
  pipeline, which answers `METHOD_HANDLER_FAILED`), so a throw out of `dispatcher.InvokeAsync` is a contract violation of a custom dispatcher and the filter has not recorded it. The dispatcher's own last-line catch (a resolver or DI fault before the pipeline) answers `METHOD_HANDLER_FAILED` as a **response**;
  since AB#7930 the dispatcher records that one event itself (octo-common-services), and the gateway, which only records exceptions it catches, never records a dispatcher answer again.
- **Caller cancellation is audited by whoever observes it (AB#7930).** A cancellation (`OperationCanceledException`, caller's token cancelled) before the dispatch started - in practice inside the
  target-access check, the only await before the dispatcher - reaches no filter and no dispatcher, so the gateway records it once: transport `gateway`, outcome `CALLER_CANCELLED`, no handler instance,
  written with `CancellationToken.None`, counted once by the recorder (`transport=gateway`), then `throw;`. Once the dispatch started (`DispatchState.Started`), the pipeline's audit filter (or the dispatcher,
  for a cancellation before the pipeline) records it and the gateway stays silent, so there is one event and one count per invocation id. Needs no new common-services API, so this repository does not wait for
  the dispatcher package.
- Tests: `GraphQL/Methods/CkMethodGatewayAuditTests` (parametrized over the five refusal paths: one record, transport `gateway`, code, invocation id; real recorder + capturing sink canary scan
  for parameter value, token and idempotency key; real in-process pipeline for the no-duplicate case; audit failure; cancelled caller; AB#7930: cancellation in the target-access check recorded once (record, real recorder + meter + canary), after the dispatch started not recorded, a dispatcher answer not recorded again; AB#7907: faults in the target-access check and in the
  dispatcher with exception text in the message (event carries no canary, caller gets the same exception), a handler that throws is audited by the filter only, meter count once). User documentation: octo-documentation `ckLanguage2/methodConsistency.md#audit` (en + de).

## Method handler diagnostics: `methodHandlers` (AB#7837, F3.2)

Root query field `MethodHandlers` (camelCase on the wire: `methodHandlers`; mounted by `GraphQL/MethodHandlersQuery.AddTo`, called from `OctoQuery`; types in
`GraphQL/MethodHandlers/`). It shows, per CK method, the handler instances of the registry of **the answering gateway replica**, their state, last seen,
heartbeat vs snapshot. **System administrators only**, checked on the root field by `Security/SystemAdministratorGuard` (moved from `SystemApi/v1/Controllers`
without a behaviour change; also used by the orphan sweep and the registry reconcile): a token issued for the system tenant (`tenant_id` = `ISystemContext.TenantId`);
a user token also needs `AdminPanelManagement`; a client-credentials token of the system tenant passes without the role. Everyone else (tenant admin of a
customer tenant, system-tenant user without the role, unauthenticated) gets `methodHandlers: null` with the error code `MethodHandlersForbidden`
(`Statics.GraphQlMethodHandlersForbidden`); the registry, the snapshot store and the CK cache are not read. No reduced view for tenant admins (platform topology
across all tenants; widening it needs a decision by Gerald).

* Source of the data: `ICkMethodHandlerRegistryDiagnostics` (octo-common-services, AB#7836: entries with `Source` Announcement/Snapshot and `State`, snapshot health,
  in-process method ids), ONE `ICkMethodHandlerSnapshotStore.LoadAsync` per request (shared collection `ck_method_handler_snapshot`, gives `persistedLastSeenUtc` by
  method id + instance id) and the system tenant's CK cache (`ICkCacheService.GetCkTypes` -> `AllMethods`, `Handler == service`). `MethodHandlersViewBuilder` builds the view once
  per request for all selected fields (`MethodHandlersRequestContext`).
* Method list = registry entries + in-process methods + CK methods with a service handler and no entry (`NOT_ANNOUNCED`, the "missing handler" case). Method `state`:
  `LIVE` (a live instance, or served in-process, then `transport = IN_PROCESS`), `NO_LIVE_HANDLER` (only expired, withdrawn or no-queue-bound instances),
  `NOT_ANNOUNCED`. Instance `state` is `LIVE|EXPIRED|WITHDRAWN|NO_QUEUE_BOUND`, `source` is `HEARTBEAT|SNAPSHOT` (a restored row no announcement confirmed yet).
  Arguments of `methods`: `search` (method id or service name, case-insensitive), `states`.
* A missing or failing snapshot store never fails the query: `persistedLastSeenUtc` is null and a warning is logged; the response never carries exception text.
  The response holds service data only (ids, service names, versions, times, tenant ids, flags), never parameters, tokens or user data
  (`GraphTypes_HoldServiceDataOnly` pins the field set).
* `answeredBy` (`dispatcherInstanceId`, `serviceVersion`, `snapshotEnabled`, `generatedAtUtc`, `snapshotLoadedAtUtc`, `snapshotRowsRestored`, `snapshotLoadFailed`,
  `lastFlushUtc`, `lastFlushFailed`) names the replica: each replica answers from its own memory, so two requests may differ; `persistedLastSeenUtc` shows what other
  replicas wrote.
* Tests: `UnitTests/GraphQL/MethodHandlersQueryTests` (real GraphQL executor with registry/store/cache doubles: access matrix, states, source, failing store, filters, field set).
  User docs: `octo-documentation` `ckLanguage2/methodHandlers.md` (en + de), section "Diagnostics". `schema.graphql` for the Studio codegen is refreshed from the running asset-repo (see the
  Studio CLAUDE.md).
