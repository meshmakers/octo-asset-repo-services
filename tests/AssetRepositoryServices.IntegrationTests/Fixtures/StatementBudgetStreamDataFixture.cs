using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
/// A <see cref="StreamDataFixture"/> whose stream data services run the statements of a recompute
/// under a budget of <see cref="StatementBudgetSeconds"/> (AB#6283): the command timeout of the
/// maintenance statements, and the driver's command timeout through the connection string, which
/// covers the statements that set none of their own (the counts). The tests' own helper connections
/// keep the unrestricted <see cref="StreamDataFixture.CrateDbConnectionString"/>.
/// </summary>
public class StatementBudgetStreamDataFixture : StreamDataFixture
{
    /// <summary>The budget of one statement, in seconds.</summary>
    public const int StatementBudgetSeconds = 1;

    /// <summary>Rows a recompute statement aims at; a statement holds at most twice that.</summary>
    public const int SliceTargetRows = 5000;

    protected override string GetServiceConnectionString(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { CommandTimeout = StatementBudgetSeconds }.ConnectionString;

    protected override void ConfigureStreamDataServices(IServiceCollection services)
    {
        base.ConfigureStreamDataServices(services);
        services.Configure<CrateRecomputeOptions>(o =>
        {
            o.CommandTimeout = TimeSpan.FromSeconds(StatementBudgetSeconds);
            o.SliceTargetRows = SliceTargetRows;
        });
    }
}
