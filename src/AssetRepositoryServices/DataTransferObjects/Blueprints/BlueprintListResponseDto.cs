namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Response for listing blueprints with pagination
/// </summary>
public class BlueprintListResponseDto
{
    /// <summary>
    ///     List of blueprints
    /// </summary>
    public List<BlueprintDto> Items { get; set; } = [];

    /// <summary>
    ///     Total count of blueprints
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    ///     Number of items skipped
    /// </summary>
    public int Skip { get; set; }

    /// <summary>
    ///     Number of items taken
    /// </summary>
    public int Take { get; set; }
}
