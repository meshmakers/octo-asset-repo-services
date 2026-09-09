using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Meshmakers.Octo.Runtime.Contracts.Serialization;

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

    /// <summary>
    /// User-column values keyed by attribute path (e.g. <c>Amount.Value</c>). Unknown keys are
    /// dropped server-side.
    /// </summary>
    /// <remarks>
    /// AB#5185: ASP.NET Core binds this body with the System.Text.Json web defaults, which leave
    /// every value of an <c>object</c>-typed dictionary as a <see cref="System.Text.Json.JsonElement"/>.
    /// Npgsql cannot bind those, so any point carrying attribute values failed with HTTP 500.
    /// <see cref="RtAttributesConverter"/> materializes the values to the same CLR scalars the
    /// pipeline path hands to the repository (<c>int</c>, <c>long</c>, <c>double</c>, <c>string</c>,
    /// <c>bool</c>, <c>DateTime</c>) — the shared <c>JsonScalar</c> rules, so integers stay integers.
    /// </remarks>
    [JsonConverter(typeof(RtAttributesConverter))]
    public IReadOnlyDictionary<string, object?> Attributes
    {
        get => _attributes;
        // System.Text.Json short-circuits a JSON null for reference-typed properties without calling
        // the converter, so "attributes": null would land here as null. Absent and null both mean
        // "no user-column values" — never hand a null dictionary to the repository.
        set => _attributes = value ?? ReadOnlyDictionary<string, object?>.Empty;
    }

    private IReadOnlyDictionary<string, object?> _attributes = ReadOnlyDictionary<string, object?>.Empty;
}
