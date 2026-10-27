using System.Security.Claims;
using FakeItEasy;
using GraphQL;
using GraphQL.Execution;
using GraphQL.Validation;
using GraphQLParser.AST;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     AB#7023 (OD-8 part 2): the smallest in-process harness. The GraphQL schema has no resolver that starts a CK method
///     yet (F3.7), so a document listener of the test plays the resolver: while a real mutation runs inside the real
///     <c>OctoSessionListener</c> transaction it calls the in-process transport, exactly where the generated per-type
///     method mutation will. Registered like the asset repository will register it: <c>AddCkInProcessMethodHandlers</c> and
///     the session probe of <c>AddCkMethodGateway</c>.
/// </summary>
public class InProcessMethodGraphQlTestFixture : GraphQlTestFixture
{
    /// <summary>
    ///     The harness method hangs on a type of its own that only the wrapped CK cache of this fixture knows, so the real
    ///     model of the other tests stays untouched.
    /// </summary>
    public const string MethodId = "AssetRepositoryIntegrationTest/MethodHarness.Touch-1";

    private const string HarnessTenant = "meshtest";
    private const string HarnessRole = "AdminPanelManagement";

    public InProcessMethodGraphQlTestFixture()
    {
        Services.TryAddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton<InProcessObservation>();
        Services.AddScoped<ObservedHandler>();
        Services.AddCkInProcessMethodHandlers(m => m.Handle<ObservedHandler>(MethodId));
        Services.AddOctoSessionAmbientStateProbe();
        // AB#7664: the host enforces the declared authorization from the definition of the method, which the in-process
        // startup validation reads from the CK cache. The harness method is declared there (wrapping the real cache), with
        // the role the harness caller holds, so the session-isolation behaviour stays what the tests are about.
        Services.Configure<CkMethodHandlerOptions>(o => o.ValidationTenantIds = [HarnessTenant]);
        DecorateCkCacheWithTheHarnessMethod();
        // AB#7026: the authorization filter is part of every host now; the harness method is not declared in the CK model.
        Services.AddSingleton<ICkMethodAuthorizationDefinitionSource, OpenDefinitionSource>();
        // Registered after OctoSessionListener (ctor of the base), so it runs once the session and the transaction exist.
        Services.AddGraphQL(b => b.AddDocumentListener<StartMethodListener>());
    }

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        // the startup validation of the in-process registration (what a service start does): fills the definition
        // (only the method services: the other initialization services of a service start need their own setup)
        var methodServices = Services
            .Where(d => d.ServiceType == typeof(IAsyncInitializationService) &&
                        d.ImplementationType?.Namespace == typeof(CkMethodHandlerOptions).Namespace)
            .Select(d => (IAsyncInitializationService)ActivatorUtilities.CreateInstance(Provider!, d.ImplementationType!))
            .OrderBy(x => x.Order);
        foreach (var service in methodServices)
        {
            await service.InitializeAsync();
        }
    }

    private void DecorateCkCacheWithTheHarnessMethod()
    {
        var descriptor = Services.Last(d => d.ServiceType == typeof(ICkCacheService));
        Services.Remove(descriptor);
        Services.AddSingleton(sp =>
        {
            var inner = (ICkCacheService)(descriptor.ImplementationInstance ??
                                          descriptor.ImplementationFactory?.Invoke(sp) ??
                                          ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!));
            var harnessType = new CkTypeGraph(new CkId<CkTypeId>("AssetRepositoryIntegrationTest/MethodHarness-1"),
                new CkCompiledTypeDto
                {
                    TypeId = new CkTypeId("MethodHarness-1"),
                    Methods =
                    [
                        new CkMethodDto
                        {
                            MethodId = "Touch-1",
                            Parameters =
                            [
                                new CkMethodParameterDto
                                    { Name = "note", ValueType = AttributeValueTypesDto.String, IsOptional = true }
                            ],
                            Result = new CkMethodResultDto { ValueType = AttributeValueTypesDto.String },
                            Authorization = new CkMethodAuthorizationDto { Roles = [HarnessRole] }
                        }
                    ]
                });
            var cache = A.Fake<ICkCacheService>(o => o.Wrapping(inner));
            A.CallTo(() => cache.IsTenantLoaded(HarnessTenant)).Returns(true);
            CkTypeGraph? found = harnessType;
            A.CallTo(() => cache.TryGetRtCkType(HarnessTenant,
                    A<RtCkId<CkTypeId>>.That.Matches(id => id.ToString().StartsWith("AssetRepositoryIntegrationTest/MethodHarness")),
                    out found))
                .Returns(true).AssignsOutAndRefParameters(harnessType);
            return cache;
        });
    }

    public InProcessObservation Observation => GetService<InProcessObservation>();

    /// <summary>
    ///     Executes <paramref name="mutation" /> and, in the request flow (inside the request
    ///     session and its transaction), starts the in-process method once.
    /// </summary>
    public async Task<(ExecutionResult Result, CkMethodResponse? Response)> ExecuteAndStartMethodInResolverAsync(
        string mutation, string variables, string note)
    {
        var listener = GetService<StartMethodListener>();
        listener.Response = null;
        listener.Note = note;
        try
        {
            var result = await ExecuteGraphQlAsync(mutation, variables);
            return (result, listener.Response);
        }
        finally
        {
            listener.Note = null;
        }
    }

    /// <summary>
    ///     Document listener that plays the resolver: it runs after <c>OctoSessionListener</c> has opened the session and
    ///     started the transaction of the mutation, in the request's flow, and starts the method there.
    /// </summary>
    private sealed class StartMethodListener(InProcessObservation observation) : IDocumentExecutionListener
    {
        public CkMethodResponse? Response { get; set; }

        public string? Note { get; set; }

        public Task AfterValidationAsync(IExecutionContext context, IValidationResult validationResult) =>
            Task.CompletedTask;

        public async Task BeforeExecutionAsync(IExecutionContext context)
        {
            if (Note == null || context.Operation?.Operation != OperationType.Mutation)
            {
                return;
            }

            var accessor = context.RequestServices!.GetRequiredService<IOctoSessionAccessor>();
            observation.HasSessionInResolver = accessor.HasSession;
            // AB#7026: every host now registers the authorization filter of the invocation pipeline (band 200), so the
            // harness that plays the resolver calls as an authorized principal, like the gateway does.
            var caller = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "sub-1"), new Claim("scope", "octo_api"), new Claim("role", HarnessRole)],
                "test"));
            using var principalScope = CkMethodPrincipalScope.Begin(CkMethodPrincipal.FromClaimsPrincipal(caller));
            Response = await context.RequestServices!.GetRequiredService<ICkInProcessMethodTransport>()
                .InvokeAsync(new CkMethodRequest("meshtest")
                {
                    InvocationId = Guid.NewGuid(),
                    CorrelationId = Guid.NewGuid(),
                    MethodId = MethodId,
                    CkTypeId = "AssetRepositoryIntegrationTest/Customer",
                    TargetRtId = "65f0c0ffee",
                    ParametersJson = $"{{\"note\":\"{Note}\"}}",
                    CallerAccessToken = "token",
                    Caller = new CkMethodCaller { SubjectId = "sub-1" },
                    DeadlineUtc = DateTime.UtcNow.AddSeconds(30)
                }, CancellationToken.None).ConfigureAwait(false);
            observation.HasSessionInResolverAfterMethod = accessor.HasSession;
        }

        public Task AfterExecutionAsync(IExecutionContext context) => Task.CompletedTask;
    }

    private sealed class OpenDefinitionSource : ICkMethodAuthorizationDefinitionSource
    {
        public Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.CkMethodDto? Find(string tenantId,
            string ckTypeId, string qualifiedMethodId) => new()
        {
            MethodId = "Touch-1",
            Authorization = new Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.CkMethodAuthorizationDto
                { Roles = ["AdminPanelManagement"] }
        };
    }

    public sealed record TouchParameters
    {
        public string? Note { get; init; }
    }

    public sealed record TouchResult
    {
        public string? Value { get; init; }
    }

    public sealed class InProcessObservation
    {
        public bool? HasSessionInResolver;
        public bool? HasSessionInResolverAfterMethod;
        public bool? HasSessionInHandler;
        public int HandlerRuns;
    }

    public sealed class ObservedHandler(IOctoSessionAccessor accessor, InProcessObservation observation)
        : ICkMethodHandler<TouchParameters, TouchResult>
    {
        public async Task<CkMethodOutcome<TouchResult>> HandleAsync(CkMethodContext<TouchParameters> context,
            CancellationToken cancellationToken)
        {
            observation.HandlerRuns++;
            observation.HasSessionInHandler = accessor.HasSession;
            await Task.Yield();
            if (context.Parameters.Note == "end-session")
            {
                // A handler must not be able to end the request transaction.
                await accessor.AbortAsync();
                await accessor.CommitAsync();
            }

            return new TouchResult { Value = "done" };
        }
    }
}
