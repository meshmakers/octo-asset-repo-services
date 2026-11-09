using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;

/// <summary>
/// Response of <c>POST archives/{archiveRtId}/insert</c> (AB#8590) and <c>POST archives/{archiveRtId}/insertTimeRange</c> (AB#7598): how many rows the archive stored and
/// which were not, with the reason, so no caller reports success for a value that was not stored.
/// </summary>
public sealed class StreamDataWriteResultRestDto
{
    /// <summary>Most skipped rows listed individually; <see cref="Skipped" /> always carries the full count.</summary>
    internal const int MaxListedRows = 1000;

    /// <summary>Rows the archive holds after the write: new, won against the stored row, or identical.</summary>
    public int Stored { get; init; }

    /// <summary>Rows that were not stored.</summary>
    public int Skipped { get; init; }

    /// <summary>
    /// Rows not stored per reason, named after the key that decided (AB#8637): <c>LowerPrecedence</c> (a ranking
    /// column of the archive's conflict precedence ranks the stored row above, also against a stored manual value
    /// when that column comes before <c>$Origin</c>), <c>ManualValueProtected</c> (<c>$Origin</c> decided: the stored
    /// row is manual, the write automated and no ranking column before <c>$Origin</c> differs; a ranking column the
    /// write does not send ties, AB#8622), <c>CkTypeMismatch</c> (not the archive's CK type).
    /// </summary>
    public IReadOnlyDictionary<string, int> SkippedByReason { get; init; } = new Dictionary<string, int>();

    /// <summary>The rows not stored, at most <see cref="MaxListedRows" />.</summary>
    public IReadOnlyList<SkippedRowRestDto> SkippedRows { get; init; } = [];

    /// <summary>Maps the engine's write result.</summary>
    public static StreamDataWriteResultRestDto From(StreamDataWriteResult result) => new()
    {
        Stored = result.Stored,
        Skipped = result.SkippedCount,
        SkippedByReason = result.SkippedByReason.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
        SkippedRows = result.Skipped.Take(MaxListedRows).Select(s => new SkippedRowRestDto
        {
            RtId = s.RtId.ToString(),
            From = s.From,
            To = s.To,
            Origin = s.Origin == ArchiveRowOrigin.Manual ? "manual" : "automated",
            Reason = s.Reason.ToString()
        }).ToList()
    };
}

/// <summary>
/// Body of the 409 of <c>POST archives/{archiveRtId}/insert</c> and <c>POST archives/{archiveRtId}/insertTimeRange</c> (AB#7598): a manual row could not be stored.
/// The SDK and the energy pipelines read <c>message</c> and <c>result</c>, so these names are the wire contract.
/// </summary>
public sealed class ManualWriteNotStoredRestDto
{
    /// <summary>Why the write is not a success.</summary>
    public required string Message { get; init; }

    /// <summary>What was stored and what was not.</summary>
    public required StreamDataWriteResultRestDto Result { get; init; }
}

/// <summary>One row of a write that was not stored (AB#7598).</summary>
public sealed class SkippedRowRestDto
{
    /// <summary>Entity the row belongs to.</summary>
    public string RtId { get; init; } = string.Empty;

    /// <summary>Timestamp of a raw row, or the inclusive window start of a time-range row (UTC).</summary>
    public DateTime From { get; init; }

    /// <summary>Exclusive window end of a time-range row (UTC); <c>null</c> for a raw row.</summary>
    public DateTime? To { get; init; }

    /// <summary><c>manual</c> or <c>automated</c>, as the caller declared it.</summary>
    public string Origin { get; init; } = "automated";

    /// <summary>Why the row was not stored.</summary>
    public string Reason { get; init; } = string.Empty;
}
