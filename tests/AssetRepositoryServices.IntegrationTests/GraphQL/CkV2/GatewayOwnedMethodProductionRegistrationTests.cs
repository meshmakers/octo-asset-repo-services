using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Methods;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.CkV2;

/// <summary>
///     AB#6893: the production service setup (<c>AddOctoAssetRepositoryServices</c>, the one the service host and every
///     fixture use) registers the in-process transport for gateway-owned methods and the remote handler host is absent. This
///     test registers nothing itself, so it fails when the call in <c>AddServices</c> is removed.
/// </summary>
public class GatewayOwnedMethodProductionRegistrationTests
{
    [Fact]
    public void AddOctoAssetRepositoryServices_RegistersTheInProcessTransport_AndNoRemoteHandlerHost()
    {
        var services = new ServiceCollection();

        services.AddRuntimeEngine()
            .AddOctoAssetRepositoryServices(_ => new OctoSystemConfiguration(),
                _ => new OctoAssetRepositoryServicesOptions());

        services.Should().Contain(d => d.ServiceType == typeof(ICkInProcessMethodTransport));
        var implementations = services.Select(d => d.ImplementationType?.Name).ToList();
        implementations.Should().Contain("CkInProcessMethodValidationService");
        implementations.Should().NotContain(["CkMethodHandlerStartupService", "CkMethodHandlerHeartbeatService",
            "CkMethodHandlerValidationService"]);
    }
}
