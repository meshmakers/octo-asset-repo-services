namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;

/// <summary>
/// REST request body for <c>POST archives/{archiveRtId}/insertTimeRange</c>: a flat array of
/// time-range data points. Mirrors <c>TimeRangeStreamDataPoint</c> but with primitive types so
/// JSON deserialisation stays straightforward (no <c>OctoObjectId</c> / <c>RtCkId&lt;CkTypeId&gt;</c>
/// surface bleed into the wire format).
/// </summary>
public sealed class InsertTimeRangePointRestDto
{
    /// <summary>Runtime id of the entity this measurement belongs to.</summary>
    public string RtId { get; set; } = string.Empty;

    /// <summary>CK type id of the entity (must match the archive's target).</summary>
    public string CkTypeId { get; set; } = string.Empty;

    /// <summary>Inclusive window start (UTC).</summary>
    public DateTime From { get; set; }

    /// <summary>Exclusive window end (UTC). Must be strictly greater than <see cref="From"/>.</summary>
    public DateTime To { get; set; }

    /// <summary>Optional human-readable name. <c>MAX(rtwellknownname)</c> on re-deliveries.</summary>
    public string? RtWellKnownName { get; set; }

    /// <summary>User-column values keyed by attribute path. Unknown keys are dropped server-side.</summary>
    public Dictionary<string, object?> Attributes { get; set; } = new();
}
