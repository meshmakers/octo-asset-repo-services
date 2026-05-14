namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     One row of the tenant's installed-blueprints view. Distinct from the
///     append-only application history.
/// </summary>
public class BlueprintInstallationDto
{
    /// <summary>Fully-qualified blueprint id ("Name-Version").</summary>
    public string BlueprintId { get; set; } = string.Empty;

    /// <summary>When this blueprint was first installed on the tenant.</summary>
    public DateTime InstalledAt { get; set; }

    /// <summary>When the installation row was last touched.</summary>
    public DateTime LastUpdatedAt { get; set; }

    /// <summary>
    ///     True when the blueprint was originally pulled in as a transitive
    ///     dependency rather than an explicit install.
    /// </summary>
    public bool IsDependency { get; set; }

    /// <summary>
    ///     Blueprint ids that came along as transitive dependencies of this row.
    /// </summary>
    public List<string> ResolvedDependencies { get; set; } = [];

    /// <summary>Optional checksum of the seed data that was last applied.</summary>
    public string? SeedDataChecksum { get; set; }
}
