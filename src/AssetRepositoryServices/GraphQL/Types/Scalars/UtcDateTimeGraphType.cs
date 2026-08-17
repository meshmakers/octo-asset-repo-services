using GraphQL.Types;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;

/// <summary>
/// Drop-in replacement for the stock <see cref="DateTimeGraphType"/> that guarantees every
/// DateTime instant crossing the GraphQL boundary carries <see cref="DateTimeKind.Utc"/>,
/// so the JSON writer always emits the ISO-8601 UTC designator (<c>Z</c>) instead of a
/// naive (semantically underspecified) date-time string (AB#4821).
///
/// The platform convention is that all persisted instants are UTC; a value arriving here
/// with <see cref="DateTimeKind.Unspecified"/> (e.g. from a database read path that did not
/// stamp the kind) is therefore *labelled* UTC, never shifted. Local-kind values are
/// converted. The same normalization applies to parsed input values, so naive input strings
/// are interpreted as UTC as well.
///
/// Keeps the wire name <c>DateTime</c> — the schema shape and client codegen are unchanged;
/// only the serialized format becomes consistently round-trip.
/// </summary>
internal sealed class UtcDateTimeGraphType : DateTimeGraphType
{
    public UtcDateTimeGraphType()
    {
        Name = "DateTime";
    }

    public override object? Serialize(object? value)
    {
        // The stock scalar formats the DateTime to a string itself, keyed on Kind — an
        // Unspecified-kind value would serialize naive. Normalize before formatting.
        return base.Serialize(value is DateTime dateTime ? EnsureUtc(dateTime) : value);
    }

    public override object? ParseValue(object? value)
    {
        var parsed = base.ParseValue(value);
        return parsed is DateTime dateTime ? EnsureUtc(dateTime) : parsed;
    }

    /// <summary>
    /// Labels Unspecified-kind values as UTC (platform convention: instants are stored in
    /// UTC), converts Local-kind values, and passes UTC-kind values through unchanged.
    /// </summary>
    internal static DateTime EnsureUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
