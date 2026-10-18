using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     A <see cref="StreamDataFixture" /> whose recomputes sweep the superseded generation at once
///     (<c>StreamData:Recompute:SweepGracePeriod</c> zero, which is today's behaviour before the grace period existed, AB#6968).
///     The default is 2 h, so a test that asserts the state right after a recompute — only the generation the pointer selects is
///     left in the table — runs on this fixture; every other test keeps the default.
/// </summary>
public class ImmediateSweepStreamDataFixture : StreamDataFixture
{
    protected override void ConfigureStreamDataServices(IServiceCollection services)
    {
        base.ConfigureStreamDataServices(services);
        services.Configure<CrateRecomputeOptions>(o => o.SweepGracePeriod = TimeSpan.Zero);
    }
}
