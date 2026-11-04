using System.Globalization;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
/// The local-day bin arguments of a downsampling query (AB#7361, engine AB#7360): <c>timeZone</c> (IANA id),
/// <c>binInterval</c> (whole days) and <c>binOrigin</c> (local time of day <c>HH:mm</c> at which a bin starts, e.g.
/// <c>06:00</c> for a gas day). All null ⇒ the fixed-width UTC bins of earlier versions (no local-day bins).
/// </summary>
internal sealed record StreamDataLocalDayBins(string? TimeZone, TimeSpan? BinInterval, TimeSpan? BinOrigin)
{
    private const long DayMs = 86_400_000;
    private const long MinuteMs = 60_000;

    private static readonly string[] OriginFormats = [@"hh\:mm", @"h\:mm"];

    /// <summary>No local-day bins: the downsampling query keeps its fixed-width UTC bins.</summary>
    public static readonly StreamDataLocalDayBins None = new(null, null, null);

    /// <summary>True when a time zone, an interval or an origin was given — the bins are local days.</summary>
    public bool HasLocalDayBins => TimeZone is not null || BinInterval is not null || BinOrigin is not null;

    /// <summary>
    /// Builds and validates the bin arguments. Argument errors the GraphQL layer can name (format of
    /// <c>binOrigin</c>, <c>binInterval</c> below one day) are an <see cref="OctoGraphQLException"/> naming the
    /// argument; the engine rules (unknown zone, origin range, interval) run through
    /// <see cref="DownsamplingBinAxis.Resolve"/> and surface as its <see cref="DownsamplingBinOptionsInvalidException"/>
    /// (STREAMDATA_ERROR naming the option), before the repository is called.
    /// </summary>
    public static StreamDataLocalDayBins Create(string? timeZone, int? binIntervalDays, string? binOrigin)
    {
        var zone = string.IsNullOrWhiteSpace(timeZone) ? null : timeZone.Trim();

        TimeSpan? interval = null;
        if (binIntervalDays is { } days)
        {
            if (days < 1)
            {
                throw new OctoGraphQLException(
                    $"downsampling: binInterval {days} is invalid. It is the width of one bin in whole local days and must be at least 1.");
            }

            interval = TimeSpan.FromDays(days);
        }

        var origin = ParseBinOrigin(binOrigin, "binOrigin");
        var bins = new StreamDataLocalDayBins(zone, interval, origin);
        if (bins.HasLocalDayBins)
        {
            DownsamplingBinAxis.Resolve(bins.ApplyTo(StreamDataDownsamplingQueryOptions.Create()));
        }

        return bins;
    }

    /// <summary>Copies the bin arguments onto the engine options.</summary>
    public StreamDataDownsamplingQueryOptions ApplyTo(StreamDataDownsamplingQueryOptions options) =>
        options.WithTimeZone(TimeZone).WithBinInterval(BinInterval).WithBinOrigin(BinOrigin);

    /// <summary>
    /// Parses a local time of day <c>HH:mm</c> (<c>00:00</c> to <c>23:59</c>). Null or blank ⇒ null (midnight).
    /// </summary>
    public static TimeSpan? ParseBinOrigin(string? value, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!TimeSpan.TryParseExact(value.Trim(), OriginFormats, CultureInfo.InvariantCulture, out var origin)
            || origin < TimeSpan.Zero || origin >= TimeSpan.FromDays(1))
        {
            throw new OctoGraphQLException(
                $"{argumentName} '{value}' is invalid. It is the local time of day at which a day starts, written HH:mm from 00:00 to 23:59 (e.g. '06:00' for a gas day).");
        }

        return origin;
    }

    /// <summary>
    /// The day start of a series query in milliseconds after local midnight (AB#7361), from
    /// <paramref name="dayStartOffsetMs"/> or derived from <paramref name="binOrigin"/> (<c>HH:mm</c>). Null when
    /// neither is given (midnight). Rejects a value outside <c>[0, 86400000)</c>, one that is not a whole minute, and
    /// two arguments that disagree.
    /// </summary>
    public static long? ResolveDayStartOffsetMs(long? dayStartOffsetMs, string? binOrigin)
    {
        if (dayStartOffsetMs is { } ms && (ms < 0 || ms >= DayMs || ms % MinuteMs != 0))
        {
            throw new OctoGraphQLException(
                $"dayStartOffsetMs {ms} is invalid. It must be a whole number of minutes in [0, {DayMs}) ms after local midnight (e.g. 21600000 = 06:00 for a gas day).");
        }

        var origin = ParseBinOrigin(binOrigin, "binOrigin");
        long? originMs = origin is { } o ? (long)o.TotalMilliseconds : null;

        if (dayStartOffsetMs is { } given && originMs is { } derived && given != derived)
        {
            throw new OctoGraphQLException(
                $"dayStartOffsetMs {given} and binOrigin '{binOrigin}' ({derived} ms) name different day starts. Supply one of them.");
        }

        return dayStartOffsetMs ?? originMs;
    }
}
