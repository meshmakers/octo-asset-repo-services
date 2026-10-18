using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     The placement matrix runs against two hosts that differ in one setting only: the sweep grace period of a recompute
///     (AB#6968). With grace <b>off</b> (zero) a recompute removes the superseded generation at once, which is how the platform
///     behaved before the grace period existed.
/// </summary>
public class PlacementMatrixGraceOffFixture : StreamDataFixture
{
    protected override void ConfigureStreamDataServices(IServiceCollection services)
    {
        base.ConfigureStreamDataServices(services);
        services.Configure<CrateRecomputeOptions>(o => o.SweepGracePeriod = TimeSpan.Zero);
    }
}

/// <summary>The default host: the sweep of a superseded generation is deferred by the grace period (2 h).</summary>
public class PlacementMatrixGraceOnFixture : StreamDataFixture
{
    protected override void ConfigureStreamDataServices(IServiceCollection services)
    {
        base.ConfigureStreamDataServices(services);
        services.Configure<CrateRecomputeOptions>(o => o.SweepGracePeriod = CrateRecomputeOptions.DefaultSweepGracePeriod);
    }
}
