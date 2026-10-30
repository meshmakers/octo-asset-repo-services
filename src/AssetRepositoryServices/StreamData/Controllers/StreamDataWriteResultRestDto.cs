using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;

/// <summary>
/// Response of <c>POST archives/{archiveRtId}/insertTimeRange</c> (AB#7598): how many rows the archive stored and
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
    /// Rows not stored per reason: <c>LowerPrecedence</c> (the archive's conflict precedence ranks the stored row
    /// above), <c>ManualValueProtected</c> (a manual value is never replaced by an automated delivery),
    /// <c>CkTypeMismatch</c> (not the archive's CK type).
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
/// Body of the 409 of <c>POST archives/{archiveRtId}/insertTimeRange</c> (AB#7598): a manual row could not be stored.
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

    /// <summary>Inclusive window start (UTC).</summary>
    public DateTime From { get; init; }

    /// <summary>Exclusive window end (UTC).</summary>
    public DateTime? To { get; init; }

    /// <summary><c>manual</c> or <c>automated</c>, as the caller declared it.</summary>
    public string Origin { get; init; } = "automated";

    /// <summary>Why the row was not stored.</summary>
    public string Reason { get; init; } = string.Empty;
}
