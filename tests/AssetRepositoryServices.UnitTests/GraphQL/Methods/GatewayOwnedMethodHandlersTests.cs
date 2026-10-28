using Meshmakers.Octo.Services.Methods;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL.Methods;

/// <summary>AB#6893: the registration seam of the methods that the asset repository owns itself.</summary>
public class GatewayOwnedMethodHandlersTests
{
    private const string MethodId = "Sample.Accounts/Account.Touch-1";

    private sealed record NoteParameters
    {
        public string? Note { get; init; }
    }

    private sealed class SampleHandler : ICkMethodHandler<NoteParameters>
    {
        public Task<CkMethodOutcome> HandleAsync(CkMethodContext<NoteParameters> context,
            CancellationToken cancellationToken) => Task.FromResult(CkMethodOutcome.Ok());
    }

    private static ServiceProvider Build(Action<ICkMethodHandlerRegistrationBuilder>? additional)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddGatewayOwnedCkMethodHandlers(additional);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Production_RegistersTheTransport_WithoutAnyMethodYet()
    {
        using var provider = Build(null);

        var transport = provider.GetRequiredService<ICkInProcessMethodTransport>();

        // no production method is gateway-owned yet; a method nobody registered still goes over the bus
        Assert.Empty(transport.MethodIds);
        Assert.False(transport.CanHandle(MethodId));
    }

    [Fact]
    public void AdditionalHandlers_AreRegisteredInProcess()
    {
        using var provider = Build(m => m.Handle<SampleHandler>(MethodId));

        var transport = provider.GetRequiredService<ICkInProcessMethodTransport>();

        Assert.Equal([MethodId], transport.MethodIds);
        Assert.True(transport.CanHandle(MethodId));
    }

    [Fact]
    public void CalledTogetherWithTheGateway_TheSessionTripwireIsRegisteredOnce()
    {
        var services = new ServiceCollection();
        services.AddCkMethodGateway();
        services.AddGatewayOwnedCkMethodHandlers();
        services.AddGatewayOwnedCkMethodHandlers();

        Assert.Single(services, d => d.ServiceType == typeof(ICkInProcessAmbientStateProbe));
    }
}
