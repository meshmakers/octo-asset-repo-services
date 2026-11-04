using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
/// AB#7361 — the local-day bin arguments of the transient downsampling query (<c>timeZone</c>, <c>binInterval</c>,
/// <c>binOrigin</c>) and the day start of <c>resolveSeriesQuery</c> (<c>dayStartOffsetMs</c> or <c>binOrigin</c>).
/// Every refusal names the argument; nothing is passed to the engine that it would reject later.
/// </summary>
public class StreamDataLocalDayBinsTests
{
    private const long GasDayMs = 21_600_000; // 06:00

    [Fact]
    public void NoArguments_KeepTheFixedWidthUtcBins()
    {
        var bins = StreamDataLocalDayBins.Create(null, null, null);

        bins.HasLocalDayBins.Should().BeFalse();
        bins.Should().Be(StreamDataLocalDayBins.None);

        var options = bins.ApplyTo(StreamDataDownsamplingQueryOptions.Create());
        options.HasLocalDayBins.Should().BeFalse("a query without the new arguments must reach the engine unchanged");
        options.TimeZone.Should().BeNull();
        options.BinInterval.Should().BeNull();
        options.BinOrigin.Should().BeNull();
    }

    [Fact]
    public void BlankTimeZone_CountsAsNotGiven()
    {
        StreamDataLocalDayBins.Create("  ", null, "").HasLocalDayBins.Should().BeFalse();
    }

    [Fact]
    public void GasDayArguments_ReachTheEngineOptions()
    {
        var bins = StreamDataLocalDayBins.Create("Europe/Vienna", 1, "06:00");

        bins.HasLocalDayBins.Should().BeTrue();
        var options = bins.ApplyTo(StreamDataDownsamplingQueryOptions.Create());
        options.TimeZone.Should().Be("Europe/Vienna");
        options.BinInterval.Should().Be(TimeSpan.FromDays(1));
        options.BinOrigin.Should().Be(TimeSpan.FromHours(6));
        options.HasLocalDayBins.Should().BeTrue();
    }

    [Theory]
    [InlineData("Europe/Vienna", null, null)]
    [InlineData(null, 7, null)]
    [InlineData(null, null, "06:00")]
    public void AnyOneArgument_MakesTheBinsLocalDays(string? timeZone, int? binInterval, string? binOrigin)
    {
        StreamDataLocalDayBins.Create(timeZone, binInterval, binOrigin).HasLocalDayBins.Should().BeTrue();
    }

    [Theory]
    [InlineData("6")]
    [InlineData("06")]
    [InlineData("24:00")]
    [InlineData("06:00:30")]
    [InlineData("-01:00")]
    [InlineData("six")]
    public void InvalidBinOrigin_IsRefused_NamingTheArgument(string binOrigin)
    {
        var act = () => StreamDataLocalDayBins.Create("Europe/Vienna", null, binOrigin);

        act.Should().Throw<OctoGraphQLException>()
            .Which.Message.Should().StartWith("binOrigin").And.Contain("HH:mm");
    }

    [Theory]
    [InlineData("6:30", 6, 30)]
    [InlineData("00:00", 0, 0)]
    [InlineData("23:59", 23, 59)]
    public void BinOrigin_AcceptsLocalTimesOfDay(string binOrigin, int hours, int minutes)
    {
        StreamDataLocalDayBins.ParseBinOrigin(binOrigin, "binOrigin")
            .Should().Be(new TimeSpan(hours, minutes, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_001)]
    [InlineData(int.MaxValue)]
    public void BinIntervalOutOfRange_IsRefused_NamingTheArgument(int binInterval)
    {
        var act = () => StreamDataLocalDayBins.Create("Europe/Vienna", binInterval, null);

        act.Should().Throw<OctoGraphQLException>().WithMessage("*binInterval*");
    }

    [Fact]
    public void UnknownTimeZone_IsRefusedByTheEngineRule_NamingTheOption()
    {
        var act = () => StreamDataLocalDayBins.Create("Europe/Atlantis", 1, "06:00");

        act.Should().Throw<DownsamplingBinOptionsInvalidException>().WithMessage("*TimeZone*Europe/Atlantis*");
    }

    [Fact]
    public void IntervalAtTheBinCap_IsAccepted()
    {
        StreamDataLocalDayBins.Create(null, 10_000, null).BinInterval.Should().Be(TimeSpan.FromDays(10_000));
    }

    // ── resolveSeriesQuery day start ──────────────────────────────────────────────────────────

    [Fact]
    public void DayStart_NeitherArgument_IsMidnight()
    {
        StreamDataLocalDayBins.ResolveDayStartOffsetMs(null, null).Should().BeNull();
    }

    [Fact]
    public void DayStart_FromMilliseconds()
    {
        StreamDataLocalDayBins.ResolveDayStartOffsetMs(GasDayMs, null).Should().Be(GasDayMs);
    }

    [Fact]
    public void DayStart_DerivedFromBinOrigin()
    {
        StreamDataLocalDayBins.ResolveDayStartOffsetMs(null, "06:00").Should().Be(GasDayMs);
    }

    [Fact]
    public void DayStart_BothArgumentsAgreeing_IsAccepted()
    {
        StreamDataLocalDayBins.ResolveDayStartOffsetMs(GasDayMs, "06:00").Should().Be(GasDayMs);
    }

    [Fact]
    public void DayStart_BothArgumentsDisagreeing_IsRefused()
    {
        var act = () => StreamDataLocalDayBins.ResolveDayStartOffsetMs(GasDayMs, "07:00");

        act.Should().Throw<OctoGraphQLException>().WithMessage("*dayStartOffsetMs*binOrigin*");
    }

    [Theory]
    [InlineData(-60_000L)]
    [InlineData(86_400_000L)]
    [InlineData(21_600_001L)]
    public void DayStart_OutOfRangeOrNotAWholeMinute_IsRefused_NamingTheArgument(long dayStartOffsetMs)
    {
        var act = () => StreamDataLocalDayBins.ResolveDayStartOffsetMs(dayStartOffsetMs, null);

        act.Should().Throw<OctoGraphQLException>()
            .Which.Message.Should().StartWith("dayStartOffsetMs").And.Contain("whole number of minutes");
    }
}
