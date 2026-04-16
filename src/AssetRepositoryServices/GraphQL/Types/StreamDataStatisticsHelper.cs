using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal static class StreamDataStatisticsHelper
{
    /// <summary>
    /// Projects the single aggregation row into per-path <see cref="StatisticsResult"/>
    /// entries, keyed by the engine's SQL alias (<c>{prefix}_{PascalCase}</c>).
    /// </summary>
    public static IEnumerable<StatisticsResult> BuildStats(
        IEnumerable<string>? paths,
        StreamDataQueryResult result,
        string prefix)
    {
        if (paths == null) yield break;
        var firstRow = result.Rows.FirstOrDefault();
        if (firstRow == null) yield break;

        foreach (var path in paths)
        {
            // The engine stores aggregation values under SQL aliases like "Avg_Voltage"
            // where the path portion is the attribute's canonical PascalCase CrateDbName.
            // (Stream-data attribute paths are flat — segments-within-paths are a non-goal,
            // see spec 2026-04-12-stream-data-casing-canonicalization-design.md).
            var pascal = path.Length > 0
                ? char.ToUpperInvariant(path[0]) + path.Substring(1)
                : path;
            var sqlAlias = $"{prefix}_{pascal}";

            object? value = null;
            if (!firstRow.Values.TryGetValue(sqlAlias, out value))
            {
                // Fallback: some callers may key by the plain path.
                firstRow.Values.TryGetValue(pascal, out value);
            }

            yield return new StatisticsResult { AttributePath = path, Value = value };
        }
    }
}
