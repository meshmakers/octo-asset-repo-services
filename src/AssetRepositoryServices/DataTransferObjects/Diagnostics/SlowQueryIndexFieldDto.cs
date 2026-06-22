namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Diagnostics;

/// <summary>
/// One field of a suggested compound index (Stage 2C / AB#4220).
/// </summary>
/// <param name="Name">Field path (e.g. <c>attributes.name.value</c>).</param>
/// <param name="Direction"><c>1</c> ascending, <c>-1</c> descending.</param>
/// <param name="Kind">
/// Passthrough lowercase of the engine's <c>SlowQueryIndexFieldKind</c> — typically
/// <c>"equality"</c> / <c>"sort"</c> / <c>"range"</c>. Lowercased rather than mapped through a
/// switch so a future engine-side kind surfaces transparently to clients.
/// </param>
public sealed record SlowQueryIndexFieldDto(
    string Name,
    int Direction,
    string Kind);
