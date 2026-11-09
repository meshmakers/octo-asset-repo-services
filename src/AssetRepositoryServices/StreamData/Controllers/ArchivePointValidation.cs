using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;

/// <summary>
/// Checks of the archive inserts (raw <c>insert</c> and <c>insertTimeRange</c>, AB#8590) that need only the request and the
/// archive snapshot. Each check answers <c>null</c> when the point is fine, otherwise the 400 message naming the point
/// (index in the batch) and the field.
/// </summary>
internal static class ArchivePointValidation
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
    /// name every ingested column (<see cref="CheckManualPointNamesEveryColumn" />). Keys are matched like the
    /// repository maps them to the storage column: segments concatenated, case-insensitive. Used by the raw insert.
    /// </summary>
    public static string? CheckColumns(int index, IReadOnlyDictionary<string, object?> attributes, ArchiveSnapshot snapshot,
        bool manual)
    {
        var ingested = IngestedColumns(snapshot);
        var computed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in snapshot.Columns.Where(c => c.IsComputed))
        {
            if (!string.IsNullOrWhiteSpace(column.Name) && TryStorageName(column.Name, out var computedName))
            {
                computed.TryAdd(computedName, column.Name);
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

        return manual ? MissingColumnsMessage(index, ingested, named.Keys, snapshot) : null;
    }

    /// <summary>
    /// AB#8590 decision D1 (raw, AC 3) and AC 9 (time range): the upsert writes every ingested column of the archive
    /// and binds an unsent one as NULL, so a manual point that leaves a column out would clear the stored value of that
    /// column silently. A manual point must therefore name every ingested column; an explicit <c>null</c> is a valid
    /// answer. Keys are matched like the storage column (segments concatenated, case-insensitive); keys that are no
    /// column are not judged here. Answers the 400 message listing the missing columns, or <c>null</c>.
    /// </summary>
    public static string? CheckManualPointNamesEveryColumn(int index, IReadOnlyDictionary<string, object?> attributes,
        ArchiveSnapshot snapshot)
    {
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in attributes.Keys)
        {
            if (TryStorageName(key, out var name))
            {
                named.Add(name);
            }
        }

        return MissingColumnsMessage(index, IngestedColumns(snapshot), named, snapshot);
    }

    /// <summary>The ingested (non-computed) columns of the archive: storage name to the column path.</summary>
    private static Dictionary<string, string> IngestedColumns(ArchiveSnapshot snapshot)
    {
        var ingested = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in snapshot.Columns.Where(c => !c.IsComputed))
        {
            if (TryStorageName(column.Path, out var name))
            {
                ingested.TryAdd(name, column.Path);
            }
        }

        return ingested;
    }

    private static string? MissingColumnsMessage(int index, Dictionary<string, string> ingested,
        IEnumerable<string> namedStorageNames, ArchiveSnapshot snapshot)
    {
        var named = namedStorageNames as ISet<string> ?? new HashSet<string>(namedStorageNames, StringComparer.Ordinal);
        var missing = ingested.Where(c => !named.Contains(c.Key)).Select(c => c.Value).ToList();
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
