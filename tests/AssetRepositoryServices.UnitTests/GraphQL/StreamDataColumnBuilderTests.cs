using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     AB#8030: the column metadata of stream-data queries matches the attribute path against the CK type's (camelCase)
///     query columns case-insensitively, like the query execution, and reports a path that exists nowhere as unknown
///     instead of STRING.
/// </summary>
public class StreamDataColumnBuilderTests
{
    private static readonly IReadOnlyCollection<CkTypeQueryColumn> TypeColumns =
    [
        new("activePower", [], AttributeValueTypesDto.Double),
        new("chargingState", [], AttributeValueTypesDto.String),
        new("sessionCount", [], AttributeValueTypesDto.Integer)
    ];

    // The archive stores the CK paths as written in its column spec; lookup is case-insensitive.
    private static readonly StreamDataFieldResolver FieldResolver = new(["activePower", "chargingState", "sessionCount"]);

    [Theory]
    [InlineData("ActivePower")]
    [InlineData("activePower")]
    [InlineData("ACTIVEPOWER")]
    public void SimpleColumn_MatchesTheCkPathInAnyCase(string path)
    {
        var column = StreamDataColumnBuilder.BuildSimpleColumn(path, TypeColumns, FieldResolver);

        column.AttributeValueType.Should().Be(AttributeValueTypesDto.Double);
        column.AttributePath.Should().Be(path, "the stored path is echoed verbatim");
        column.AggregationType.Should().Be(AggregationTypesDto.None);
        column.UserContext.Should().BeNull();
    }

    [Theory]
    [InlineData("ActivePower", AggregationTypesDto.Maximum, AttributeValueTypesDto.Double)]
    [InlineData("ActivePower", AggregationTypesDto.Sum, AttributeValueTypesDto.Double)]
    [InlineData("SessionCount", AggregationTypesDto.Minimum, AttributeValueTypesDto.Integer)]
    [InlineData("ChargingState", AggregationTypesDto.Count, AttributeValueTypesDto.Integer)]
    [InlineData("SessionCount", AggregationTypesDto.Average, AttributeValueTypesDto.Double)]
    [InlineData("activePower", AggregationTypesDto.Integral, AttributeValueTypesDto.Double)]
    [InlineData("chargingState", AggregationTypesDto.StateDuration, AttributeValueTypesDto.Integer)]
    public void AggregationColumn_UsesTheCaseInsensitiveSourceType(
        string path, AggregationTypesDto aggregation, AttributeValueTypesDto expected)
    {
        var column = StreamDataColumnBuilder.BuildAggregationColumn(path, aggregation, TypeColumns, FieldResolver);

        column.AttributeValueType.Should().Be(expected);
        column.AggregationType.Should().Be(aggregation);
        column.UserContext.Should().BeNull();
    }

    [Fact]
    public void FindCkColumn_PrefersTheExactMatch()
    {
        IReadOnlyCollection<CkTypeQueryColumn> columns =
        [
            new("value", [], AttributeValueTypesDto.Double),
            new("Value", [], AttributeValueTypesDto.Integer)
        ];

        StreamDataColumnBuilder.FindCkColumn("Value", columns)!.ValueType.Should().Be(AttributeValueTypesDto.Integer);
        StreamDataColumnBuilder.FindCkColumn("value", columns)!.ValueType.Should().Be(AttributeValueTypesDto.Double);
        StreamDataColumnBuilder.FindCkColumn("VALUE", columns)!.ValueType.Should().Be(AttributeValueTypesDto.Double);
    }

    [Theory]
    [InlineData("timestamp")]
    [InlineData("rtId")]
    [InlineData("RTID")]
    public void KnownNonCkField_KeepsString(string path)
    {
        var column = StreamDataColumnBuilder.BuildSimpleColumn(path, TypeColumns, FieldResolver);

        column.AttributeValueType.Should().Be(AttributeValueTypesDto.String);
        column.UserContext.Should().BeNull("the archive knows the field, it is not unknown");
    }

    [Fact]
    public void UnknownPath_IsMarkedUnknown_ForSimpleColumns()
    {
        var column = StreamDataColumnBuilder.BuildSimpleColumn("DoesNotExist", TypeColumns, FieldResolver);

        column.UserContext.Should().BeSameAs(UnknownStreamDataColumn.Instance);
        column.AttributePath.Should().Be("DoesNotExist");
    }

    [Theory]
    [InlineData(AggregationTypesDto.Count)]
    [InlineData(AggregationTypesDto.Average)]
    [InlineData(AggregationTypesDto.Maximum)]
    [InlineData(AggregationTypesDto.Integral)]
    public void UnknownPath_IsMarkedUnknown_ForEveryAggregation(AggregationTypesDto aggregation)
    {
        var column = StreamDataColumnBuilder.BuildAggregationColumn("DoesNotExist", aggregation, TypeColumns, FieldResolver);

        column.UserContext.Should().BeSameAs(UnknownStreamDataColumn.Instance);
        column.AggregationType.Should().Be(aggregation);
    }

    [Fact]
    public void ArchiveColumnThatIsNoCkAttribute_KeepsString()
    {
        // e.g. a rollup's physical column or a computed column: known to the archive, not a CK attribute.
        var resolver = new StreamDataFieldResolver(["activePower", "activepower_avg_sum"]);

        var column = StreamDataColumnBuilder.BuildSimpleColumn("activepower_avg_sum", TypeColumns, resolver);

        column.AttributeValueType.Should().Be(AttributeValueTypesDto.String);
        column.UserContext.Should().BeNull();
    }
}
