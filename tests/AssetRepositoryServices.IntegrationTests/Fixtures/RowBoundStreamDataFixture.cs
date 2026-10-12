using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
/// A <see cref="StreamDataFixture"/> whose recomputes cut their copy, sweep and reclaim statements
/// at <see cref="SliceTargetRows"/> rows instead of the production default of 100,000 (AB#6283), so
/// that a rollup of a few hundred rows already runs through many slices.
/// </summary>
public class RowBoundStreamDataFixture : StreamDataFixture
{
    /// <summary>Rows a recompute statement aims at; a statement holds at most twice that.</summary>
    public const int SliceTargetRows = 20;

    protected override void ConfigureStreamDataServices(IServiceCollection services)
    {
        base.ConfigureStreamDataServices(services);
        services.Configure<CrateRecomputeOptions>(o => o.SliceTargetRows = SliceTargetRows);
    }
}
