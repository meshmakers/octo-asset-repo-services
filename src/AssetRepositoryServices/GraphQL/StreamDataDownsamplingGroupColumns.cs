using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
/// The <c>groupByColumnPaths</c> argument of the transient <c>downsampling</c> query (AB#8647): one result row per
/// series and bin instead of one total per bin. The engine groups by the paths
/// (<see cref="StreamDataDownsamplingQueryOptions.GroupByColumnPaths"/>, AB#7360) and, with local-day bins, fills
/// every bin of the axis per series (<c>COUNT</c> 0, other aggregations null). The group key is returned as a cell of
/// each row, after the <c>timestamp</c> cell.
/// <para>
/// v1 groups by <c>rtId</c> only: the engine's downsampling row carries the series identity (<c>StreamDataRow.RtId</c>,
/// also on a synthesized empty bin) but no other group value, so any other path would return an empty key cell. Other
/// paths are refused with a message naming them.
/// </para>
/// </summary>
internal static class StreamDataDownsamplingGroupColumns
{
    /// <summary>The wire name of the series identity (the engine column is <c>rtid</c>).</summary>
    public const string RtIdPath = "rtId";

    /// <summary>The group paths the downsampling query supports.</summary>
    public static readonly IReadOnlyList<string> SupportedPaths = [RtIdPath];

    /// <summary>
    /// Validates the requested group paths. Null or empty means no grouping (one row per bin, unchanged). The paths are
    /// returned in the canonical spelling (<c>rtId</c>), duplicates removed.
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? paths)
    {
        if (paths is null)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var path in paths)
        {
            var supported = SupportedPaths.FirstOrDefault(p => string.Equals(p, path?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (supported is null)
            {
                throw new OctoGraphQLException(
                    $"downsampling: groupByColumnPaths '{path}' is not supported. A downsampling query groups by " +
                    $"{string.Join(", ", SupportedPaths.Select(p => $"'{p}'"))} only; use groupingAggregation to group by other columns.");
            }

            if (!result.Contains(supported))
            {
                result.Add(supported);
            }
        }

        return result;
    }

    /// <summary>
    /// Adds the group key of every row under the canonical key of its mapping, so the cell of the group column has a
    /// value. A key the engine already put into the row values is kept; <c>rtId</c> is taken from the row identity.
    /// </summary>
    public static IReadOnlyList<StreamDataRow> AddGroupValues(
        IReadOnlyList<StreamDataRow> rows, IReadOnlyList<ColumnNameMapping> groupMappings)
    {
        if (groupMappings.Count == 0)
        {
            return rows;
        }

        return rows.Select(row =>
        {
            var values = new Dictionary<string, object?>(row.Values);
            foreach (var mapping in groupMappings)
            {
                if (values.TryGetValue(mapping.Canonical, out var existing) && existing is not null)
                {
                    continue;
                }

                if (string.Equals(mapping.Wire, RtIdPath, StringComparison.OrdinalIgnoreCase))
                {
                    values[mapping.Canonical] = row.RtId is { } rtId && rtId != OctoObjectId.Empty ? rtId.ToString() : null;
                }
            }

            return new StreamDataRow
            {
                RtId = row.RtId,
                CkTypeId = row.CkTypeId,
                Timestamp = row.Timestamp,
                RtWellKnownName = row.RtWellKnownName,
                RtCreationDateTime = row.RtCreationDateTime,
                RtChangedDateTime = row.RtChangedDateTime,
                Values = values
            };
        }).ToList();
    }
}
