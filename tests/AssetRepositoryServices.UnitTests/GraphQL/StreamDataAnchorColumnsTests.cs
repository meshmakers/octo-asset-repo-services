using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     AB#7812: the split of anchor columns, the column order, the display-only refusal and, above all, the batching: one
///     anchor load per page of rows, never one per row.
/// </summary>
public class StreamDataAnchorColumnsTests
{
    private static readonly StreamDataFieldResolver FieldResolver = new(["voltage"]);

    private static StreamDataRow Row(OctoObjectId? rtId, double voltage) => new()
    {
        RtId = rtId,
        Values = new Dictionary<string, object?> { ["voltage"] = voltage }
    };

    private static RtEntity Anchor(OctoObjectId id, string number) =>
        new(new RtCkId<CkTypeId>("Test/MeteringPoint"), id,
            new Dictionary<string, object?> { ["MeteringPointNumber"] = number });

    [Theory]
    [InlineData("anchor.obisCode", true)]
    [InlineData("ANCHOR.obisCode", true)]
    [InlineData("anchor.amount.unitOfMeasure", true)]
    [InlineData("anchorage", false)]
    [InlineData("voltage", false)]
    [InlineData("", false)]
    public void IsAnchor_RecognisesTheReservedPrefix(string column, bool expected)
    {
        StreamDataAnchorColumns.IsAnchor(column).Should().Be(expected);
    }

    [Fact]
    public void PathOf_RemovesThePrefix()
    {
        StreamDataAnchorColumns.PathOf("anchor.amount.unitOfMeasure").Should().Be("amount.unitOfMeasure");
    }

    [Fact]
    public void Plan_SplitsTheAnchorColumnsOffAndAsksTheEngineForTheRtId()
    {
        var plan = StreamDataAnchorColumns.Plan(["window_start", "anchor.obisCode", "voltage", "anchor.unit"], FieldResolver);

        plan.HasAnchors.Should().BeTrue();
        plan.AnchorColumns.Should().Equal("anchor.obisCode", "anchor.unit");
        plan.ArchiveColumns.Should().Equal("window_start", "voltage");
        plan.EngineColumns.Should().Equal("window_start", "voltage", Constants.RtId);
    }

    [Fact]
    public void Plan_DoesNotAskTwiceForAnRtIdThatIsAlreadyListed()
    {
        var plan = StreamDataAnchorColumns.Plan(["rtId", "anchor.obisCode"], FieldResolver);

        plan.EngineColumns.Should().Equal("rtId");
    }

    [Fact]
    public void Plan_WithoutAnchorColumns_ChangesNothing()
    {
        var requested = new[] { "voltage", "rtId" };

        var plan = StreamDataAnchorColumns.Plan(requested, FieldResolver);

        plan.HasAnchors.Should().BeFalse();
        plan.EngineColumns.Should().BeSameAs(requested);
        plan.ArchiveColumns.Should().BeSameAs(requested);
    }

    [Fact]
    public void MergeMappings_PutsTheAnchorColumnsBackAtTheirPlace()
    {
        var plan = StreamDataAnchorColumns.Plan(["anchor.obisCode", "voltage", "anchor.unit"], FieldResolver);
        var enumId = new CkId<CkEnumId>("Test/UnitOfMeasure");

        var merged = plan.MergeMappings([new ColumnNameMapping("voltage", "voltage")],
            path => path == "unit" ? enumId : null);

        merged.Select(m => m.Wire).Should().Equal("anchor.obisCode", "voltage", "anchor.unit");
        merged.Select(m => m.Canonical).Should().Equal("anchor.obisCode", "voltage", "anchor.unit");
        merged.Select(m => m.EnumId).ToArray().Should().Equal(new CkId<CkEnumId>?[] { null, null, enumId });
    }

    [Theory]
    [InlineData("a field filter")]
    [InlineData("a sort order")]
    [InlineData("an aggregation")]
    public void EnsureNone_RefusesAnAnchorColumnAndNamesColumnAndLimit(string usage)
    {
        var act = () => StreamDataAnchorColumns.EnsureNone(["voltage", "anchor.obisCode"], usage);

        act.Should().Throw<OctoGraphQLException>()
            .Which.Message.Should().Contain("anchor.obisCode").And.Contain(usage).And.Contain("display-only");
    }

    [Fact]
    public void EnsureNone_IgnoresPathsWithoutTheAnchorPrefixAndNull()
    {
        StreamDataAnchorColumns.EnsureNone(["voltage", "rtId"], "a field filter");
        StreamDataAnchorColumns.EnsureNone(null, "a field filter");
    }

    [Fact]
    public async Task BuildRows_LoadsTheAnchorsOfAPageWithOneCall()
    {
        var m1 = OctoObjectId.GenerateNewId();
        var m2 = OctoObjectId.GenerateNewId();
        // Six rows, two distinct anchors.
        var rows = new[] { Row(m1, 1), Row(m2, 2), Row(m1, 3), Row(m2, 4), Row(m1, 5), Row(m2, 6) };
        var plan = StreamDataAnchorColumns.Plan(["voltage", "anchor.number"], FieldResolver);
        var calls = new List<IReadOnlyList<OctoObjectId>>();

        var result = await StreamDataAnchorColumns.BuildRowsAsync(rows,
            plan.MergeMappings([new ColumnNameMapping("voltage", "voltage")], _ => null), plan,
            ids =>
            {
                calls.Add(ids);
                return Task.FromResult<IReadOnlyDictionary<OctoObjectId, RtEntity>>(
                    new Dictionary<OctoObjectId, RtEntity> { [m1] = Anchor(m1, "AT-1"), [m2] = Anchor(m2, "AT-2") });
            },
            (entity, _) => entity.GetAttributeValueOrDefault("MeteringPointNumber"));

        calls.Should().ContainSingle("one page of rows causes one anchor lookup, not one per row");
        calls[0].Should().BeEquivalentTo([m1, m2], "every anchor is asked for once");
        result.Select(r => r.Values["anchor.number"]).Should().Equal("AT-1", "AT-2", "AT-1", "AT-2", "AT-1", "AT-2");
        result.Select(r => r.Values["voltage"]).Should().Equal(1.0, 2.0, 3.0, 4.0, 5.0, 6.0);
    }

    [Fact]
    public async Task BuildRows_GivesNullCellsForAnAnchorTheCallerDoesNotGet()
    {
        var visible = OctoObjectId.GenerateNewId();
        var hidden = OctoObjectId.GenerateNewId();
        var plan = StreamDataAnchorColumns.Plan(["voltage", "anchor.number"], FieldResolver);

        var result = await StreamDataAnchorColumns.BuildRowsAsync([Row(visible, 1), Row(hidden, 2), Row(null, 3)],
            plan.MergeMappings([new ColumnNameMapping("voltage", "voltage")], _ => null), plan,
            _ => Task.FromResult<IReadOnlyDictionary<OctoObjectId, RtEntity>>(
                new Dictionary<OctoObjectId, RtEntity> { [visible] = Anchor(visible, "AT-1") }),
            (entity, _) => entity.GetAttributeValueOrDefault("MeteringPointNumber"));

        result.Select(r => r.Values["anchor.number"]).ToArray().Should().Equal(new object?[] { "AT-1", null, null });
    }

    [Fact]
    public async Task BuildRows_WithoutAnchorColumns_NeverLoadsAnything()
    {
        var plan = StreamDataAnchorColumns.Plan(["voltage"], FieldResolver);
        var loads = 0;

        var result = await StreamDataAnchorColumns.BuildRowsAsync(
            [Row(OctoObjectId.GenerateNewId(), 1), Row(OctoObjectId.GenerateNewId(), 2)],
            [new ColumnNameMapping("voltage", "voltage")], plan,
            _ =>
            {
                loads++;
                return Task.FromResult<IReadOnlyDictionary<OctoObjectId, RtEntity>>(new Dictionary<OctoObjectId, RtEntity>());
            },
            (_, _) => null);

        loads.Should().Be(0);
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task BuildRows_OfAPageWithoutRtIds_DoesNotCallTheRepository()
    {
        var plan = StreamDataAnchorColumns.Plan(["voltage", "anchor.number"], FieldResolver);
        var loads = 0;

        var result = await StreamDataAnchorColumns.BuildRowsAsync([Row(null, 1)],
            plan.MergeMappings([new ColumnNameMapping("voltage", "voltage")], _ => null), plan,
            _ =>
            {
                loads++;
                return Task.FromResult<IReadOnlyDictionary<OctoObjectId, RtEntity>>(new Dictionary<OctoObjectId, RtEntity>());
            },
            (_, _) => "never");

        loads.Should().Be(0);
        result.Single().Values["anchor.number"].Should().BeNull();
    }
}
