using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

public class StreamDataDownsamplingReducersTests
{
    private static RtQueryColumnDto Col(string path, AttributeValueTypesDto type) => new()
    {
        AttributePath = path,
        AttributeValueType = type,
        AggregationType = AggregationTypesDto.None
    };

    [Theory]
    [InlineData(AttributeValueTypesDto.Integer)]
    [InlineData(AttributeValueTypesDto.Integer64)]
    [InlineData(AttributeValueTypesDto.Double)]
    public void NumericColumn_ProducesAvgMinMaxEnvelope(AttributeValueTypesDto type)
    {
        var reducers = StreamDataDownsamplingReducers.Synthesize(
            ["amount.value"], [Col("amount.value", type)]);

        reducers.Should().BeEquivalentTo(new[]
        {
            new AggregationColumn("amount.value", AggregationFunction.Average),
            new AggregationColumn("amount.value", AggregationFunction.Minimum),
            new AggregationColumn("amount.value", AggregationFunction.Maximum)
        }, o => o.WithStrictOrdering());
    }

    [Theory]
    [InlineData(AttributeValueTypesDto.String)]
    [InlineData(AttributeValueTypesDto.Enum)]
    [InlineData(AttributeValueTypesDto.Boolean)]
    [InlineData(AttributeValueTypesDto.DateTime)]
    public void NonNumericColumn_ProducesSingleMaxRepresentative(AttributeValueTypesDto type)
    {
        var reducers = StreamDataDownsamplingReducers.Synthesize(
            ["obisCode"], [Col("obisCode", type)]);

        reducers.Should().ContainSingle()
            .Which.Function.Should().Be(AggregationFunction.Maximum);
    }

    [Fact]
    public void MixedColumnSet_NumericGetsEnvelope_NonNumericGetsMax()
    {
        var reducers = StreamDataDownsamplingReducers.Synthesize(
            ["amount.value", "obisCode"],
            [
                Col("amount.value", AttributeValueTypesDto.Double),
                Col("obisCode", AttributeValueTypesDto.String)
            ]);

        reducers.Should().HaveCount(4);
        reducers.Where(r => r.AttributePath == "amount.value").Should().HaveCount(3);
        reducers.Where(r => r.AttributePath == "obisCode").Should().ContainSingle()
            .Which.Function.Should().Be(AggregationFunction.Maximum);
    }

    [Fact]
    public void NonChartableShapes_AreSkipped()
    {
        var reducers = StreamDataDownsamplingReducers.Synthesize(
            ["rec", "arr"],
            [
                Col("rec", AttributeValueTypesDto.Record),
                Col("arr", AttributeValueTypesDto.StringArray)
            ]);

        reducers.Should().BeEmpty();
    }

    [Fact]
    public void ColumnWithoutTypeMetadata_IsSkipped()
    {
        var reducers = StreamDataDownsamplingReducers.Synthesize(["unknown"], []);

        reducers.Should().BeEmpty();
    }
}
