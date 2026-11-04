using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.StreamData;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
/// AB#7361 (review of engine PR #339): <c>resolveSeriesQuery</c> must pass the day start of the request to the engine
/// (<see cref="SeriesResolutionRequest.QueryDayStartOffsetMs"/>). Under PerQuery a CalendarDay rung only answers a
/// request with its own day start, so without the setter every query counted as a midnight query and a gas-day rollup
/// was never chosen. The request is built by the resolver's own mapping and resolved by the real
/// <see cref="SeriesResolutionService"/> over a family with a midnight and a gas-day rollup (Europe/Vienna, Count).
/// </summary>
public class ResolveSeriesQueryDayStartTests
{
    private const long GasDayMs = 21_600_000; // 06:00
    private const string Zone = "Europe/Vienna";
    private const string Path = "Amount.Value";

    private static readonly OctoObjectId BaseRtId = OctoObjectId.GenerateNewId();
    private static readonly OctoObjectId MidnightRtId = OctoObjectId.GenerateNewId();
    private static readonly OctoObjectId GasDayRtId = OctoObjectId.GenerateNewId();
    private static readonly RtCkId<CkTypeId> CkType = new("Test/Measurement");

    [Fact]
    public async Task GasDayRequest_ReadsTheGasDayRollup()
    {
        var result = await ResolveAsync(Input(dayStartOffsetMs: GasDayMs));

        result.ArchiveRtId.Should().Be(GasDayRtId);
    }

    [Fact]
    public async Task GasDayRequest_ByBinOrigin_ReadsTheGasDayRollup()
    {
        var result = await ResolveAsync(Input(binOrigin: "06:00"));

        result.ArchiveRtId.Should().Be(GasDayRtId);
    }

    [Fact]
    public async Task RequestWithoutDayStart_ReadsTheMidnightRollup_NeverGasDays()
    {
        var result = await ResolveAsync(Input());

        result.ArchiveRtId.Should().Be(MidnightRtId);
    }

    [Fact]
    public void Mapping_SetsTheDayStart_AndKeepsTheOtherFields()
    {
        var input = Input(dayStartOffsetMs: GasDayMs);

        var request = StreamDataQuery.BuildSeriesResolutionRequest(input);

        request.QueryDayStartOffsetMs.Should().Be(GasDayMs);
        request.QueryTimeZone.Should().Be(Zone);
        request.BaseArchiveRtId.Should().Be(BaseRtId);
        request.ComparisonPolicy.Should().Be(SeriesComparisonPolicy.PerQuery);
        request.RequiredAggregation.Should().Be(CkRollupFunction.Count);
    }

    [Fact]
    public void Mapping_WithoutDayStart_LeavesItUnset()
    {
        StreamDataQuery.BuildSeriesResolutionRequest(Input()).QueryDayStartOffsetMs.Should().BeNull(
            "a request without the new arguments must reach the engine exactly as before");
    }

    private static ResolveSeriesQueryInputDto Input(long? dayStartOffsetMs = null, string? binOrigin = null) => new()
    {
        BaseArchiveRtId = BaseRtId,
        From = new DateTime(2026, 3, 1, 5, 0, 0, DateTimeKind.Utc),
        To = new DateTime(2026, 5, 1, 4, 0, 0, DateTimeKind.Utc),
        TargetPoints = 61,
        RequiredAggregation = CkRollupFunction.Count,
        SourcePath = Path,
        TimeZone = Zone,
        DayStartOffsetMs = dayStartOffsetMs,
        BinOrigin = binOrigin,
    };

    private static Task<SeriesResolutionResult> ResolveAsync(ResolveSeriesQueryInputDto input)
    {
        var archiveStore = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => archiveStore.GetAsync(BaseRtId))
            .Returns(new ArchiveSnapshot(BaseRtId, CkType, CkArchiveStatus.Activated, "raw", []));

        var rollupStore = A.Fake<IRollupArchiveRuntimeStore>();
        A.CallTo(() => rollupStore.EnumerateAsync())
            .ReturnsLazily(() => Rollups([DayRollup(MidnightRtId, null), DayRollup(GasDayRtId, GasDayMs)]));

        var service = new SeriesResolutionService(archiveStore, new RollupDependencyGraph(rollupStore));
        return service.ResolveAsync(StreamDataQuery.BuildSeriesResolutionRequest(input));
    }

    private static RollupArchiveSnapshot DayRollup(OctoObjectId rtId, long? dayStartOffsetMs) =>
        new(rtId, CkType, CkArchiveStatus.Activated, rtId.ToString(),
            [new RollupSourceReference(BaseRtId)],
            TimeSpan.FromDays(1), TimeSpan.FromHours(1), null,
            [new CkRollupAggregationSpec(Path, CkRollupFunction.Count, null)],
            null)
        {
            BucketAlignment = BucketAlignment.CalendarDay,
            ReferenceTimeZone = Zone,
            DayStartOffsetMs = dayStartOffsetMs,
        };

#pragma warning disable CS1998 // async iterator over an in-memory list
    private static async IAsyncEnumerable<RollupArchiveSnapshot> Rollups(IEnumerable<RollupArchiveSnapshot> rollups)
#pragma warning restore CS1998
    {
        foreach (var rollup in rollups)
        {
            yield return rollup;
        }
    }
}
