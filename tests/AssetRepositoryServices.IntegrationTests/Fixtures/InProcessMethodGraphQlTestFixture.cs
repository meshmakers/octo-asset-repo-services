using System.Security.Claims;
using GraphQL;
using GraphQL.Execution;
using GraphQL.Validation;
using GraphQLParser.AST;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
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
    public const string MethodId = "AssetRepositoryIntegrationTest/Customer.Touch-1";

    public InProcessMethodGraphQlTestFixture()
    {
        Services.TryAddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton<InProcessObservation>();
        Services.AddScoped<ObservedHandler>();
        Services.AddCkInProcessMethodHandlers(m => m.Handle<ObservedHandler>(MethodId));
        Services.AddOctoSessionAmbientStateProbe();
        // AB#7026: the authorization filter is part of every host now; the harness method is not declared in the CK model.
        Services.AddSingleton<ICkMethodAuthorizationDefinitionSource, OpenDefinitionSource>();
        // Registered after OctoSessionListener (ctor of the base), so it runs once the session and the transaction exist.
        Services.AddGraphQL(b => b.AddDocumentListener<StartMethodListener>());
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
                [new Claim("sub", "sub-1"), new Claim("scope", "octo_api"), new Claim("role", "AdminPanelManagement")],
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
