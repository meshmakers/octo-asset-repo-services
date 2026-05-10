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
            // After T17 the engine stores aggregation values under SQL aliases like "Avg_voltage"
            // where the path portion is the attribute's camelCase CrateDB column name (mirrors
            // the BSON convention; stream-data attribute paths are flat — segments-within-paths
            // are a non-goal).
            var camel = path.Length > 0
                ? char.ToLowerInvariant(path[0]) + path.Substring(1)
                : path;
            var sqlAlias = $"{prefix}_{camel}";

            object? value = null;
            if (!firstRow.Values.TryGetValue(sqlAlias, out value))
            {
                // Fallback: some callers may key by the plain camelCase path.
                firstRow.Values.TryGetValue(camel, out value);
            }

            yield return new StatisticsResult { AttributePath = path, Value = value };
        }
    }
}
