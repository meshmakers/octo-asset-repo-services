namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Result of a blueprint apply operation.
/// </summary>
public class BlueprintApplyResultDto
{
    /// <summary>Whether the apply completed successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Tenant the blueprint was applied to.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Fully-qualified blueprint id that was applied.</summary>
    public string BlueprintId { get; set; } = string.Empty;

    /// <summary>Application mode used: Initial or ReApply.</summary>
    public string ApplicationMode { get; set; } = string.Empty;

    /// <summary>Number of seed-data files that were imported.</summary>
    public int SeedDataFilesApplied { get; set; }

    /// <summary>CK models loaded as part of the application.</summary>
    public List<string> LoadedCkModels { get; set; } = [];

    /// <summary>Warnings raised during the operation.</summary>
    public List<string> Warnings { get; set; } = [];
}
