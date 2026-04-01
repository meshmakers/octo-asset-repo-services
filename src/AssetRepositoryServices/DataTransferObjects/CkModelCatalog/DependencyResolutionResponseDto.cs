namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;

/// <summary>
///     Response for the dependency resolution endpoint.
///     Contains the full dependency tree with install/update actions.
/// </summary>
public class DependencyResolutionResponseDto
{
    /// <summary>
    ///     The root model with its resolved dependency tree
    /// </summary>
    public DependencyResolutionItemDto RootModel { get; set; } = null!;
}
