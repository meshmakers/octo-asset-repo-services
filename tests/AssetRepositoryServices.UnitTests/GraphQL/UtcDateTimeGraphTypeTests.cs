using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
/// Every DateTime instant leaving the GraphQL layer must carry <see cref="DateTimeKind.Utc"/>
/// so the JSON writer emits the ISO-8601 UTC designator (<c>Z</c>) — a naive date-time string
/// is semantically underspecified and JavaScript's <c>new Date(...)</c> parses it as browser-local
/// time (AB#4821, observed as mixed naive/Z timestamps within one downsampling result set).
/// Unspecified-kind values are labelled UTC (platform convention), never shifted.
/// </summary>
public class UtcDateTimeGraphTypeTests
{
    private readonly UtcDateTimeGraphType _scalar = new();

    [Fact]
    public void KeepsWireName_DateTime()
    {
        _scalar.Name.Should().Be("DateTime");
    }

    [Fact]
    public void Serialize_UnspecifiedKind_IsLabelledUtcWithoutShifting()
    {
        var naive = new DateTime(2026, 8, 3, 2, 25, 0, DateTimeKind.Unspecified);

        var result = _scalar.Serialize(naive);

        AssertUtcWire(result, new DateTime(2026, 8, 3, 2, 25, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Serialize_UtcKind_KeepsInstant()
    {
        var utc = new DateTime(2026, 8, 3, 2, 25, 0, DateTimeKind.Utc);

        var result = _scalar.Serialize(utc);

        AssertUtcWire(result, utc);
    }

    [Fact]
    public void Serialize_LocalKind_IsConvertedToUtc()
    {
        var local = new DateTime(2026, 8, 3, 4, 25, 0, DateTimeKind.Local);

        var result = _scalar.Serialize(local);

        AssertUtcWire(result, local.ToUniversalTime());
    }

    /// <summary>
    /// The wire contract under test: the serialized form must carry an explicit UTC designator
    /// and round-trip to the expected instant — regardless of whether the scalar hands the JSON
    /// writer a string or a UTC-kind DateTime.
    /// </summary>
    private static void AssertUtcWire(object? serialized, DateTime expectedUtc)
    {
        serialized.Should().NotBeNull();
        switch (serialized)
        {
            case string wire:
                wire.Should().EndWith("Z");
                DateTime.Parse(wire, null, System.Globalization.DateTimeStyles.AdjustToUniversal)
                    .Should().Be(expectedUtc);
                break;
            case DateTime dateTime:
                dateTime.Kind.Should().Be(DateTimeKind.Utc);
                dateTime.Should().Be(expectedUtc);
                break;
            default:
                Assert.Fail($"Unexpected serialized type {serialized!.GetType()}");
                break;
        }
    }

    [Fact]
    public void Serialize_Null_PassesThrough()
    {
        _scalar.Serialize(null).Should().BeNull();
    }

    [Fact]
    public void ParseValue_NaiveIsoString_IsInterpretedAsUtc()
    {
        var result = _scalar.ParseValue("2026-08-03T02:25:00");

        var dateTime = result.Should().BeOfType<DateTime>().Subject;
        dateTime.Kind.Should().Be(DateTimeKind.Utc);
        dateTime.Should().Be(new DateTime(2026, 8, 3, 2, 25, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ParseValue_ZonedIsoString_IsConvertedToUtc()
    {
        var result = _scalar.ParseValue("2026-08-03T04:25:00+02:00");

        var dateTime = result.Should().BeOfType<DateTime>().Subject;
        dateTime.Kind.Should().Be(DateTimeKind.Utc);
        dateTime.Should().Be(new DateTime(2026, 8, 3, 2, 25, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void SimpleScalar_Serialize_NormalizesUnspecifiedDateTimeToUtc()
    {
        var scalar = new SimpleScalarType();
        var naive = new DateTime(2026, 8, 3, 2, 25, 0, DateTimeKind.Unspecified);

        var result = scalar.Serialize(naive);

        var dateTime = result.Should().BeOfType<DateTime>().Subject;
        dateTime.Kind.Should().Be(DateTimeKind.Utc);
        dateTime.Should().Be(new DateTime(2026, 8, 3, 2, 25, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void SimpleScalar_Serialize_NonDateTimeValues_PassThrough()
    {
        var scalar = new SimpleScalarType();

        scalar.Serialize(22.5).Should().Be(22.5);
        scalar.Serialize("text").Should().Be("text");
        scalar.Serialize(null).Should().BeNull();
    }
}
