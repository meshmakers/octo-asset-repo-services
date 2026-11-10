using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
/// AB#8647 — the <c>groupByColumnPaths</c> argument of the transient downsampling query: no argument keeps the ungrouped
/// query, <c>rtId</c> is accepted in any spelling, every other path is refused naming it, and the series key reaches the
/// row values (also on a synthesized empty bin).
/// </summary>
public class StreamDataDownsamplingGroupColumnsTests
{
    private static readonly ColumnNameMapping RtIdMapping = new("rtid", "rtId");

    [Fact]
    public void NoArgument_OrAnEmptyList_MeansNoGrouping()
    {
        StreamDataDownsamplingGroupColumns.Normalize(null).Should().BeEmpty();
        StreamDataDownsamplingGroupColumns.Normalize([]).Should().BeEmpty();
    }

    [Theory]
    [InlineData("rtId")]
    [InlineData("RTID")]
    [InlineData(" rtid ")]
    public void RtId_InAnySpelling_IsNormalizedOnce(string path)
    {
        StreamDataDownsamplingGroupColumns.Normalize([path, "rtId"]).Should().Equal("rtId");
    }

    [Theory]
    [InlineData("Voltage")]
    [InlineData("timestamp")]
    [InlineData("row_origin")]
    public void AnyOtherPath_IsRefused_NamingThePathAndTheSupportedOne(string path)
    {
        var act = () => StreamDataDownsamplingGroupColumns.Normalize(["rtId", path]);

        act.Should().Throw<OctoGraphQLException>()
            .Which.Message.Should().Contain($"'{path}'").And.Contain("'rtId'").And.Contain("groupingAggregation");
    }

    [Fact]
    public void AddGroupValues_PutsTheRowIdentityUnderTheGroupColumn_AlsoForAnEmptyBin()
    {
        var series = OctoObjectId.GenerateNewId();
        var populated = new StreamDataRow
        {
            RtId = series,
            Timestamp = new DateTime(2026, 3, 28, 5, 0, 0, DateTimeKind.Utc),
            Values = new Dictionary<string, object?> { ["timestamp"] = DateTime.UtcNow, ["voltage_count"] = 23L },
        };
        var empty = new StreamDataRow
        {
            RtId = series,
            Values = new Dictionary<string, object?> { ["voltage_count"] = 0L },
        };

        var rows = StreamDataDownsamplingGroupColumns.AddGroupValues([populated, empty], [RtIdMapping]);

        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => (string?)r.Values["rtid"] == series.ToString());
        rows[0].Values["voltage_count"].Should().Be(23L);
        rows[1].Values["voltage_count"].Should().Be(0L);
        rows[0].Timestamp.Should().Be(populated.Timestamp);
    }

    [Fact]
    public void AddGroupValues_WithoutGrouping_ReturnsTheEngineRowsUnchanged()
    {
        IReadOnlyList<StreamDataRow> engineRows = [new StreamDataRow { Values = new Dictionary<string, object?>() }];

        StreamDataDownsamplingGroupColumns.AddGroupValues(engineRows, []).Should().BeSameAs(engineRows);
    }

    [Fact]
    public void AddGroupValues_ARowWithoutIdentity_GetsANullKey()
    {
        var rows = StreamDataDownsamplingGroupColumns.AddGroupValues(
            [new StreamDataRow { Values = new Dictionary<string, object?>() }], [RtIdMapping]);

        rows.Single().Values["rtid"].Should().BeNull();
    }
}
