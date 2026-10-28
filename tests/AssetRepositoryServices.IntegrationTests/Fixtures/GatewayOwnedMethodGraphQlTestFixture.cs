using FakeItEasy;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Methods;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     AB#6893 (F3.7): a method that the gateway owns itself (<c>handler: service</c>) runs as in-process handler. The
///     host is the production one: real schema and MongoDB, the real gateway, the real dispatcher with the in-process
///     preference, the real invocation pipeline and authorization, and the registration seam of the asset repository
///     (<c>AddGatewayOwnedCkMethodHandlers</c>). Test-only parts: the sample handler for the test model's
///     <c>AccessTestAccount.ResetPassword-1</c> and a bus double that must never be touched.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class GatewayOwnedMethodGraphQlTestFixture : GraphQlTestFixture
{
    /// <summary>
    ///     The test model's instance method (<c>handler: service</c>, role <c>AccessTestAdmin</c>, <c>allowSelf</c>, declared
    ///     error <c>PASSWORD_POLICY_VIOLATION</c>, <c>timeoutSeconds: 5</c>).
    /// </summary>
    public const string MethodId = "AssetRepositoryIntegrationTest/AccessTestAccount.ResetPassword-1";

    public const string DeclaredError = "PASSWORD_POLICY_VIOLATION";

    public GatewayOwnedMethodGraphQlTestFixture()
    {
        Bus = A.Fake<IDistributionEventHubService>();
        Services.AddSingleton(Bus);
        Services.AddSingleton<SampleObservation>();
        Services.AddGatewayOwnedCkMethodHandlers(m => m.Handle<SampleResetPasswordHandler>(MethodId));
        Services.Configure<CkMethodHandlerOptions>(o => o.ValidationTenantIds = [SystemTenantId]);
    }

    /// <summary>The event hub the dispatcher would use for a remote handler; an in-process method never touches it.</summary>
    public IDistributionEventHubService Bus { get; }

    public SampleObservation Observation => GetService<SampleObservation>();

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        // The startup validation of the in-process registrations (what a service start does): it reads the definition of
        // the method from the CK cache of the tenant and fills it in. Only the method services of the service start.
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

    /// <summary>Services that the in-process registration put into the host (to prove what it did not register).</summary>
    public IEnumerable<ServiceDescriptor> RegisteredServices => Services;

    public sealed class SampleObservation
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public string? LastTenantId { get; set; }

        public string? LastTargetRtId { get; set; }

        public string? LastCallerSubject { get; set; }

        public bool? HasSessionInHandler { get; set; }

        public void Reset()
        {
            Volatile.Write(ref _runs, 0);
            LastTenantId = null;
            LastTargetRtId = null;
            LastCallerSubject = null;
            HasSessionInHandler = null;
        }

        public void Run() => Interlocked.Increment(ref _runs);
    }

    /// <summary>Parameters of <c>ResetPassword-1</c> (the generated record of a real model has the same shape).</summary>
    public sealed record ResetPasswordParameters
    {
        public string? CurrentPassword { get; init; }

        public string? NewPassword { get; init; }
    }

    /// <summary>
    ///     The documented sample of an in-process handler: a thin typed adapter. The new password selects the scenario so
    ///     that one handler covers every outcome of the envelope: <c>weak</c> is the declared error, <c>boom</c> an
    ///     unexpected exception, <c>hang</c> a handler that runs until its deadline; anything else succeeds.
    /// </summary>
    public sealed class SampleResetPasswordHandler(IOctoSessionAccessor sessionAccessor, SampleObservation observation)
        : ICkMethodHandler<ResetPasswordParameters>
    {
        public async Task<CkMethodOutcome> HandleAsync(CkMethodContext<ResetPasswordParameters> context,
            CancellationToken cancellationToken)
        {
            observation.Run();
            observation.LastTenantId = context.TenantId;
            observation.LastTargetRtId = context.TargetRtId;
            observation.LastCallerSubject = context.CallerPrincipal?.Subject;
            observation.HasSessionInHandler = sessionAccessor.HasSession;

            switch (context.Parameters.NewPassword)
            {
                case "weak":
                    return CkMethodOutcome.Fail(DeclaredError, ("minLength", "12"));
                case "boom":
                    throw new InvalidOperationException("sample handler failure");
                case "hang":
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    break;
            }

            return CkMethodOutcome.Ok();
        }
    }
}
