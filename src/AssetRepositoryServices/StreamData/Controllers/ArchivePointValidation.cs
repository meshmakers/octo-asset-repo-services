using System.Text.Json;
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
    /// Brings a row-key time (the raw <c>timestamp</c>, the time-range <c>from</c> and <c>to</c>) to UTC (a value without
    /// offset is read as UTC, a value with offset is converted) and refuses a missing value and sub-millisecond precision:
    /// the row identity holds milliseconds, so two times that differ below a millisecond would silently address the same
    /// row. <paramref name="field" /> is the wire name the message names.
    /// </summary>
    public static string? NormalizeTimestamp(int index, DateTime timestamp, out DateTime utc, string field = "timestamp")
    {
        utc = timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp,
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
        };

        if (utc == default)
        {
            return $"Point {index}: {field} is missing.";
        }

        return utc.Ticks % TimeSpan.TicksPerMillisecond != 0
            ? $"Point {index}: {field} '{utc:O}' has sub-millisecond precision. An archive row is identified in " +
              $"milliseconds; round {field} to whole milliseconds."
            : null;
    }

    /// <summary>
    /// Refuses attribute keys that are no ingested column of the archive (unknown, or a computed column the archive
    /// calculates itself), keys that address the same column twice, and — for a manual point — a point that does not
    /// name every ingested column (AB#8590 decision D1 for the raw insert, AC 9 for the time range: the origin is stored
    /// per row, so a manual point marks the whole row manual and protects every column of it, and it has to state each
    /// value it protects; an explicit <c>null</c> is a valid answer. Before engine-mongodb AB#8622 an unsent column was
    /// also cleared; since then it keeps its stored value). Ranking columns are exempt from that rule (below). Keys are matched like the repository maps them to the storage column: segments
    /// concatenated, case-insensitive. Used by both the raw <c>insert</c> and <c>insertTimeRange</c>, for automated and
    /// manual points: an unknown or computed key would otherwise be dropped by the repository without a word.
    /// <para>
    /// A ranking column (a column key of the archive's <c>ConflictPrecedence</c>; <c>$Origin</c> is no column) cannot be
    /// null (engine-mongodb AB#8622, decided by Reimar 2026-11-09): an explicit <c>null</c> on one is refused for automated
    /// and manual points. A ranking column a point leaves out keeps its stored value and counts as that value when the
    /// write is ranked. That is why a manual point need not name the ranking columns (decided by Odin 2026-11-09): a
    /// correction of a row whose ranking column is stored as NULL can only leave it out, since it cannot send null.
    /// </para>
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

        var ranking = RankingColumns(snapshot);
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

            if (IsNull(attributes[key]) && ranking.Contains(name))
            {
                return $"Point {index}: attribute '{key}' is a ranking column of archive '{snapshot.RtId}' (ConflictPrecedence) " +
                       "and cannot be null. Send a value, or leave the attribute out to keep the stored value.";
            }
        }

        return manual ? MissingColumnsMessage(index, ingested, ranking, named.Keys, snapshot) : null;
    }

    /// <summary>The storage names of the archive's ranking columns (column keys of the conflict precedence).</summary>
    private static HashSet<string> RankingColumns(ArchiveSnapshot snapshot)
    {
        var ranking = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in snapshot.ConflictPrecedence.Where(k => !k.IsOrigin))
        {
            if (TryStorageName(key.Column, out var name))
            {
                ranking.Add(name);
            }
        }

        return ranking;
    }

    private static bool IsNull(object? value) =>
        value is null or DBNull || value is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };

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
        HashSet<string> ranking, IEnumerable<string> namedStorageNames, ArchiveSnapshot snapshot)
    {
        var named = namedStorageNames as ISet<string> ?? new HashSet<string>(namedStorageNames, StringComparer.Ordinal);
        var missing = ingested.Where(c => !named.Contains(c.Key) && !ranking.Contains(c.Key)).Select(c => c.Value).ToList();
        return missing.Count == 0
            ? null
            : $"Point {index}: a manual point must name every ingested column of archive '{snapshot.RtId}' " +
              $"(null is allowed; ranking columns may be left out and keep their stored value). Missing: {Describe(missing)}.";
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
