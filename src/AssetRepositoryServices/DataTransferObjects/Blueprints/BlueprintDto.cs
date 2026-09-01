namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Represents a blueprint from a catalog
/// </summary>
public class BlueprintDto
{
    /// <summary>
    ///     Full blueprint ID including version (e.g., "MyBlueprint-1.0.0")
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Blueprint name without version
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Blueprint version
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    ///     Optional description of the blueprint
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Name of the catalog containing this blueprint
    /// </summary>
    public string CatalogName { get; set; } = string.Empty;

    /// <summary>
    ///     Blueprint dependency id strings as declared in the blueprint's <c>blueprintDependencies</c>
    ///     (e.g. "MeshmakersAccounting-[1.0.0,)"). Empty when the blueprint declares none. Lets a
    ///     consumer filter the catalog to add-ons that depend on a given base blueprint.
    /// </summary>
    public List<string> BlueprintDependencies { get; set; } = [];

    /// <summary>
    ///     CK model dependency id strings as declared in the blueprint's <c>ckModelDependencies</c>
    ///     (e.g. "Meshmakers.Accounting-[1.24.0,2.0)"). Empty when the blueprint declares none.
    /// </summary>
    public List<string> CkModelDependencies { get; set; } = [];
}
