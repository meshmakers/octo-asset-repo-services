using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Meshmakers.Octo.Runtime.Contracts.Serialization;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;

/// <summary>
/// REST request body for <c>POST archives/{archiveRtId}/insert</c> (AB#8590): a flat array of data points of a raw
/// archive. Mirrors <c>StreamDataPoint</c> with primitive types, like <see cref="InsertTimeRangePointRestDto" />.
/// </summary>
/// <remarks>
/// <b>Row identity:</b> a raw row is identified by archive + <see cref="RtId" /> + <see cref="Timestamp" /> (UTC,
/// millisecond precision); the CK type is fixed per archive. A second write with the same identity replaces the stored
/// row, subject to the archive's conflict precedence (a manual row is never replaced by an automated delivery).
/// Protection is per row: a manual correction of one attribute also protects the other attributes of that row.
/// </remarks>
public sealed class InsertRawPointRestDto
{
    /// <summary>Runtime id of the entity this measurement belongs to.</summary>
    public string RtId { get; set; } = string.Empty;

    /// <summary>CK type id of the entity in its unversioned form (must match the archive's target).</summary>
    public string CkTypeId { get; set; } = string.Empty;

    /// <summary>
    /// Time of the measurement (UTC). Part of the row identity, stored with millisecond precision; a timestamp with
    /// sub-millisecond precision is refused with HTTP 400. A value without offset is read as UTC.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Optional human-readable name of the entity.</summary>
    public string? RtWellKnownName { get; set; }

    /// <summary>
    /// Ingested column values keyed by attribute path (e.g. <c>Amount.Value</c>), as in the archive's columns. A key that
    /// is no ingested column of the archive, or that names a computed column, refuses the batch with HTTP 400. A manual
    /// point must name every ingested column except the ranking columns (an explicit <c>null</c> is allowed), so a manual
    /// row states every value it protects. An attribute the point does not name keeps the stored value of its column, an attribute sent as
    /// <c>null</c> clears it; a new row stores NULL for every column the point does not name (AB#8622). A ranking column
    /// of the archive's conflict precedence cannot be null: an explicit <c>null</c> on one is refused with HTTP 400. A
    /// manual point may leave a ranking column out; it then keeps its stored value (AB#8622).
    /// </summary>
    /// <remarks>
    /// Materialized by <see cref="RtAttributesConverter" /> to CLR scalars, the same rules as
    /// <see cref="InsertTimeRangePointRestDto.Attributes" /> (AB#5185).
    /// </remarks>
    [JsonConverter(typeof(RtAttributesConverter))]
    public IReadOnlyDictionary<string, object?> Attributes
    {
        get => _attributes;
        // Absent and null both mean "no values" — never hand a null dictionary to the repository.
        set => _attributes = value ?? ReadOnlyDictionary<string, object?>.Empty;
    }

    private IReadOnlyDictionary<string, object?> _attributes = ReadOnlyDictionary<string, object?>.Empty;

    /// <summary>
    /// <c>manual</c> or <c>automated</c>, case-insensitive; absent means automated. Any other value refuses the batch
    /// with HTTP 400. The writer of a manual row is taken from the caller's token, never from the body (AB#7593).
    /// </summary>
    public string? Origin { get; set; }

    /// <summary>Optional free-text reason of a manual write, stored as given. Only allowed with origin <c>manual</c>.</summary>
    public string? Reason { get; set; }
}
