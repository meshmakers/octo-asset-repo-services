# Gateway-owned CK methods as in-process handlers (AB#6893, F3.7)

A CK method that the asset repository implements itself (`execution.handler: service` on a model the gateway hosts, for
example the planned `Tenant.*` and `Archive.*` methods) is registered as an **in-process handler**: it runs in the
asset-repo process through the common invocation pipeline, without the event bus. No production method is gateway-owned
yet; the seam is in place and covered by a test-only sample.

## What the platform guarantees

- **No bus hop, no announcement.** The dispatcher (`AddCkMethodDispatcher`) asks the in-process transport first. A registered
  method is never published to the bus and never announced as a remote handler (no `CkMethodHandlerStartupService`, no
  heartbeat, no queue). A method registered in-process and as a remote handler stops the service start.
- **Same answer as on the bus.** Success, declared error, `METHOD_HANDLER_FAILED` (exception, with a `correlationId` and without
  the exception text) and `METHOD_TIMEOUT` have the same envelope as the remote handler host of the same handler class.
- **Same pipeline, no bypass.** Audit, authorization and idempotency filters run for the in-process call as for the bus call, and
  the host evaluates the declared `authorization` of the method (roles, scopes, `allowSelf`, default-deny) before the handler
  is created: a call without a principal, or with one that the method does not admit, is `METHOD_FORBIDDEN` and the handler
  does not run. The principal is the one the gateway established after it authenticated the HTTP request
  (`CkMethodPrincipalScope`). `AddCkMethodCallerTokenValidation` is **not** needed in the asset repository: it validates the
  token a remote handler host receives over the bus, and the asset repository hosts no remote handler.
- **Outside the request session.** The handler runs in a new DI scope, detached from the request's execution context:
  `IOctoSessionAccessor.HasSession` is `false` inside it (AB#7023). The handler resolves its tenant from `context.TenantId`.

## Registering a gateway-owned method

1. Write the handler as a thin typed adapter (`ICkMethodHandler<TParameters>` or `ICkMethodHandler<TParameters, TResult>`);
   everything that matters lives in a transport-independent service.
2. Add one line to `GraphQL/Methods/GatewayOwnedMethodHandlers.Register`:

```csharp
methods.Handle<ArchiveActivateHandler>(SystemCkIds.ArchiveActivateMethodId);   // version-less qualified method id
```

3. Declare `authorization` in the CK model. A method without one is refused for every caller.

The registration is validated at service start against the CK cache of the system tenant (method exists, parameter and result
shapes match, sensitive parameters and declared errors are read from the definition); a mismatch stops the start with a
message that names the method.

## The sample (test-only)

`tests/AssetRepositoryServices.IntegrationTests/Fixtures/GatewayOwnedMethodGraphQlTestFixture.cs` registers
`SampleResetPasswordHandler` for the test model's `AssetRepositoryIntegrationTest/AccessTestAccount.ResetPassword-1`
(`handler: service`, role `AccessTestAdmin`, declared error `PASSWORD_POLICY_VIOLATION`, `timeoutSeconds: 5`) through the
production seam:

```csharp
Services.AddGatewayOwnedCkMethodHandlers(m => m.Handle<SampleResetPasswordHandler>(MethodId));
```

The new password selects the outcome (`weak` declared error, `boom` exception, `hang` runs until the deadline, otherwise
success). `GraphQL/CkV2/GatewayOwnedMethodInProcessTests` calls it through the real GraphQL mutation, gateway and dispatcher and
asserts: handler ran in-process without a session, the bus double was never touched, the four envelopes equal the bus path,
and the no-bypass cases (no role, no principal, wrong role). Unit tests: `GatewayOwnedMethodHandlersTests`.
