namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Diagnostics;

/// <summary>
/// One row of the Stage 3 / AB#4224 unused-index analysis surface. Aggregates MongoDB's
/// <c>$indexStats</c> across replica-set hosts and pairs the figure with a paste-ready
/// <c>dropIndex</c> mongosh command when the index is droppable.
/// </summary>
/// <param name="CollectionName">Tenant collection the index is on (e.g. <c>rt_entities</c>).</param>
/// <param name="IndexName">Index name as MongoDB sees it (e.g. <c>attributes.name.value_1</c>).</param>
/// <param name="KeySpec">Canonical JSON of the index keys (e.g. <c>{"attributes.name.value": 1}</c>).</param>
/// <param name="OpsCount">Sum of <c>accesses.ops</c> across replica-set hosts.</param>
/// <param name="SinceUtc">Earliest <c>accesses.since</c> across hosts — the worst-case observation window.</param>
/// <param name="AgeDays">Days between <see cref="SinceUtc"/> and the moment the snapshot was taken.</param>
/// <param name="IsBuiltin"><c>true</c> for the <c>_id_</c> index — read-only on the Studio surface.</param>
/// <param name="DropShellCommand">
/// Paste-ready mongosh literal for dropping the index. <c>null</c> when <see cref="IsBuiltin"/>.
/// </param>
/// <param name="Status">
/// Classification — <c>"builtin"</c> / <c>"unused"</c> / <c>"lowUsage"</c> / <c>"used"</c>.
/// Flattened from the engine enum so future engine-side renames don't break the API contract.
/// </param>
public sealed record IndexUsageEntryDto(
    string CollectionName,
    string IndexName,
    string KeySpec,
    long OpsCount,
    DateTimeOffset SinceUtc,
    int AgeDays,
    bool IsBuiltin,
    string? DropShellCommand,
    string Status);
