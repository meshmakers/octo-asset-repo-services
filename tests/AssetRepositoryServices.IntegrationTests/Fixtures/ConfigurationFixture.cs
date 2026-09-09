using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
/// Fixture that loads configuration from appsettings.test.json.
/// </summary>
public abstract class ConfigurationFixture : ServiceCollectionFixture
{
    private readonly IntegrationTestConfiguration _configuration;

    /// <summary>
    /// Unique per fixture instance so unrelated fixtures can share one MongoDB server
    /// (<see cref="SharedMongoDbContainer" />) without colliding on the same database (AB#5118).
    /// </summary>
    public string SystemDatabaseName { get; } = $"assetrepointegrationtests{Guid.NewGuid():N}";

    /// <summary>
    /// Same idea for the CrateDB side (AB#5118): stream data is isolated by schema and
    /// <c>TenantSchema.SchemaName</c> derives that schema from the tenant id, so a per-fixture
    /// system tenant id gives every fixture its own schema on the one shared CrateDB container.
    /// Must stay purely alphanumeric - the stream-data tests embed the tenant id verbatim as the
    /// schema identifier of their <c>REFRESH TABLE</c> statements.
    /// </summary>
    public string SystemTenantId { get; } = $"octosystem{Guid.NewGuid():N}";

    protected ConfigurationFixture()
    {
        _configuration = new IntegrationTestConfiguration();

        Services.Configure<IntegrationTestOptions>(options =>
            _configuration.GetSection("integrationTest").Bind(options));
    }

    protected T GetOptions<T>(string sectionName)
    {
        var option = Activator.CreateInstance<T>();
        _configuration.GetSection(sectionName).Bind(option);
        return option!;
    }
}
