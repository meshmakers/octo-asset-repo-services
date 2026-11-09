using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;

/// <summary>
/// Checks of a raw-archive insert (AB#8590) that need only the request and the archive snapshot. Each check answers
/// <c>null</c> when the point is fine, otherwise the 400 message naming the point (index in the batch) and the field.
/// </summary>
internal static class RawArchivePointValidation
{
    /// <summary>
    /// Brings the timestamp to UTC (a value without offset is read as UTC, a value with offset is converted) and refuses
    /// sub-millisecond precision: the row identity holds milliseconds, so two timestamps that differ below a millisecond
    /// would silently address the same row.
    /// </summary>
    public static string? NormalizeTimestamp(int index, DateTime timestamp, out DateTime utc)
    {
        utc = timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp,
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
        };

        if (utc == default)
        {
            return $"Point {index}: timestamp is missing.";
        }

        return utc.Ticks % TimeSpan.TicksPerMillisecond != 0
            ? $"Point {index}: timestamp '{utc:O}' has sub-millisecond precision. A raw row is identified by archive, rtId " +
              "and timestamp in milliseconds; round the timestamp to whole milliseconds."
            : null;
    }

    /// <summary>
    /// Refuses attribute keys that are no ingested column of the archive (unknown, or a computed column the archive
    /// calculates itself), keys that address the same column twice, and — for a manual point — a point that does not
    /// name every ingested column. Keys are matched like the repository maps them to the storage column: segments
    /// concatenated, case-insensitive.
    /// </summary>
    public static string? CheckColumns(int index, IReadOnlyDictionary<string, object?> attributes, ArchiveSnapshot snapshot,
        bool manual)
    {
        var ingested = new Dictionary<string, string>(StringComparer.Ordinal);
        var computed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in snapshot.Columns)
        {
            if (column.IsComputed)
            {
                if (!string.IsNullOrWhiteSpace(column.Name) && TryStorageName(column.Name, out var computedName))
                {
                    computed.TryAdd(computedName, column.Name);
                }
            }
            else if (TryStorageName(column.Path, out var name))
            {
                ingested.TryAdd(name, column.Path);
            }
        }

        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in attributes.Keys)
        {
            if (!TryStorageName(key, out var name))
            {
                return $"Point {index}: attribute '{key}' is not a valid attribute path.";
            }

            if (computed.TryGetValue(name, out var computedColumn))
            {
                return $"Point {index}: attribute '{key}' is the computed column '{computedColumn}' of archive " +
                       $"'{snapshot.RtId}'. The archive calculates it from the other columns; it cannot be written.";
            }

            if (!ingested.ContainsKey(name))
            {
                return $"Point {index}: attribute '{key}' is not a column of archive '{snapshot.RtId}'. " +
                       $"Ingested columns: {Describe(ingested.Values)}.";
            }

            if (!named.TryAdd(name, key))
            {
                return $"Point {index}: attributes '{named[name]}' and '{key}' address the same column.";
            }
        }

        if (!manual)
        {
            return null;
        }

        // AB#8590 decision D1: the upsert writes every ingested column, so an unsent attribute would become NULL.
        // A correction must say what each column holds; an explicit null is a valid answer.
        var missing = ingested.Where(c => !named.ContainsKey(c.Key)).Select(c => c.Value).ToList();
        return missing.Count == 0
            ? null
            : $"Point {index}: a manual point must name every ingested column of archive '{snapshot.RtId}' " +
              $"(null is allowed). Missing: {Describe(missing)}.";
    }

    private static string Describe(IEnumerable<string> paths)
    {
        var list = paths.Select(p => $"'{p}'").ToList();
        return list.Count == 0 ? "none" : string.Join(", ", list);
    }

    /// <summary>The storage column of an attribute path, as the CrateDB repository derives it (ColumnNameMapper).</summary>
    private static bool TryStorageName(string path, out string name)
    {
        name = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var segments = path.Split('.');
        if (segments.Any(s => s.Length == 0))
        {
            return false;
        }

        name = string.Concat(segments).ToLowerInvariant();
        return true;
    }
}
