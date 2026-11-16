using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     A <see cref="StreamDataFixture" /> whose recomputes sweep the superseded generation at once (sweep grace period zero,
///     AB#6968) and that keeps the production late-data debounce (<c>StreamData:Recompute:LateDataDebounce</c>, default
///     5 min, AB#9016). Tests on this fixture check, end to end through MongoDB, what the production default does on the first
///     drain after a write: a late automated delivery is held back, a manual write is not (AB#9099).
/// </summary>
public class DefaultDebounceStreamDataFixture : StreamDataFixture
{
    protected override void ConfigureStreamDataServices(IServiceCollection services)
    {
        base.ConfigureStreamDataServices(services);
        services.Configure<CrateRecomputeOptions>(o => o.SweepGracePeriod = TimeSpan.Zero);
    }
}
