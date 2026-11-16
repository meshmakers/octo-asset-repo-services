using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     A <see cref="StreamDataFixture" /> whose recomputes sweep the superseded generation at once
///     (<c>StreamData:Recompute:SweepGracePeriod</c> zero, which is today's behaviour before the grace period existed, AB#6968).
///     The default is 2 h, so a test that asserts the state right after a recompute — only the generation the pointer selects is
///     left in the table — runs on this fixture; every other test keeps the default.
///     <para>
///     The late-data debounce (<c>StreamData:Recompute:LateDataDebounce</c>, AB#9016, default 5 min) is zero here as well: the
///     recompute tests of this collection write a late value and drive the production drain
///     (<see cref="Meshmakers.Octo.Runtime.Contracts.StreamData.IRecomputeOrchestrator.TickAsync"/>) at once, and with the default
///     the drain holds the late window back for five minutes and runs nothing (AB#9099). Zero runs the scoped, in-place refresh on
///     the same tick, so the tests still check where the late values end up. The hold itself, the coalescing of the late
///     deliveries of one interval into one job and the restart behaviour are covered with a fake clock by
///     <c>RecomputeLateDeliveryTests</c> in octo-construction-kit-engine.
///     </para>
/// </summary>
public class ImmediateSweepStreamDataFixture : StreamDataFixture
{
    protected override void ConfigureStreamDataServices(IServiceCollection services)
    {
        base.ConfigureStreamDataServices(services);
        services.Configure<CrateRecomputeOptions>(o => o.SweepGracePeriod = TimeSpan.Zero);
        services.Configure<RecomputeLateDataOptions>(o => o.LateDataDebounce = TimeSpan.Zero);
    }
}
