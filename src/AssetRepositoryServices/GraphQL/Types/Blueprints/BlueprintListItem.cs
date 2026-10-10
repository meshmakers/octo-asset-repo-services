using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// A <see cref="BlueprintDto"/> produced by the GraphQL <c>list</c> / <c>search</c> queries. The declared
/// dependencies are not part of the catalog listing — they live in the blueprint manifest, which for GitHub
/// catalogs costs one HTTP GET per blueprint (AB#6306). They are therefore resolved lazily, only when the client
/// selects <c>blueprintDependencies</c> or <c>ckModelDependencies</c>, and at most once per item.
/// </summary>
internal sealed class BlueprintListItem : BlueprintDto
{
    private readonly Lazy<Task<BlueprintDependencies>> _dependencies;

    public BlueprintListItem(Func<Task<BlueprintDependencies>> dependencyLoader)
    {
        _dependencies = new Lazy<Task<BlueprintDependencies>>(dependencyLoader);
    }

    /// <summary>
    /// Loads (on first use) and returns the declared dependencies of this blueprint.
    /// </summary>
    public Task<BlueprintDependencies> GetDependenciesAsync() => _dependencies.Value;
}

/// <summary>
/// Declared dependency id strings of a blueprint.
/// </summary>
internal sealed record BlueprintDependencies(List<string> Blueprints, List<string> CkModels)
{
    public static BlueprintDependencies Empty => new([], []);
}
