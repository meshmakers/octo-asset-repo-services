namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     A <see cref="StreamDataFixture" /> on a CrateDB container of one exact image tag (AB#5490), joined to the network of
///     the matrix S3 store. The version matrix needs two of them in one test (the source series and the 6.x target), so the
///     tag is a property of the fixture class, not of the process-wide container of the other stream-data tests.
/// </summary>
public abstract class VersionedCrateStreamDataFixture : StreamDataFixture
{
    private CrateVersionContainer? _crate;

    /// <summary>The image tag, for example <c>crate:5.9.10</c>.</summary>
    public abstract string Image { get; }

    /// <summary>The CrateDB version as the server reports it (<c>SELECT version['number'] FROM sys.nodes</c>).</summary>
    public string ServerVersion { get; private set; } = string.Empty;

    protected override async Task<string> GetCrateDbConnectionStringAsync()
    {
        _crate = await CrateVersionMatrixInfrastructure.StartCrateAsync(Image);
        return _crate.ConnectionString;
    }

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();
        ServerVersion = await ReadServerVersionAsync();
    }

    protected override async Task DisposeServicesAsync()
    {
        try
        {
            await base.DisposeServicesAsync();
        }
        finally
        {
            if (_crate is not null)
            {
                await _crate.DisposeAsync();
                _crate = null;
            }
        }
    }

    private async Task<string> ReadServerVersionAsync()
    {
        await using var conn = new Npgsql.NpgsqlConnection(CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("SELECT version['number'] FROM sys.nodes LIMIT 1", conn);
        return Convert.ToString(await cmd.ExecuteScalarAsync(), global::System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}

/// <summary>CrateDB 5.9.10 (source series).</summary>
public sealed class Crate59StreamDataFixture : VersionedCrateStreamDataFixture
{
    public override string Image => CrateVersionMatrixInfrastructure.Crate59;
}

/// <summary>CrateDB 5.10.10 (source series).</summary>
public sealed class Crate510StreamDataFixture : VersionedCrateStreamDataFixture
{
    public override string Image => CrateVersionMatrixInfrastructure.Crate510;
}

/// <summary>CrateDB 6.4.6 (target of the matrix).</summary>
public sealed class Crate6StreamDataFixture : VersionedCrateStreamDataFixture
{
    public override string Image => CrateVersionMatrixInfrastructure.Crate6;
}
