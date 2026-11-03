using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
/// AB#8168: the wire key of an aggregation column. A single StateDuration column keeps
/// <c>{column}_stateduration</c> (Studio and MeshBoards read it); several on one attribute are told apart by
/// their comparison value, with the same rule the engine uses for its row keys.
/// </summary>
public class StreamDataAggregationMappingTests
{
    private readonly StreamDataFieldResolver _resolver = new(["position", "power"]);

    private static AggregationColumn State(string path, string value) =>
        new(path, AggregationFunction.StateDuration, value);

    [Fact]
    public void SingleStateDuration_KeepsTheContractKey()
    {
        var mappings = _resolver.ResolveAggregationMappings([State("position", "0")]);

        mappings.Select(m => m.Wire).Should().Equal("position_stateduration");
        mappings.Select(m => m.Canonical).Should().Equal("position_stateduration");
    }

    [Fact]
    public void TwoStateDurationsOnOneAttribute_GetDistinctKeys()
    {
        var mappings = _resolver.ResolveAggregationMappings([State("position", "0"), State("position", "100")]);

        mappings.Select(m => m.Wire).Should().Equal("position_stateduration_0", "position_stateduration_100");
        mappings.Select(m => m.Canonical).Should().Equal(mappings.Select(m => m.Wire));
    }

    [Fact]
    public void StateDurationsOnDifferentAttributes_KeepTheContractKey()
    {
        var mappings = _resolver.ResolveAggregationMappings([State("position", "0"), State("power", "0")]);

        mappings.Select(m => m.Wire).Should().Equal("position_stateduration", "power_stateduration");
    }

    [Fact]
    public void OtherFunctions_AreUnchanged_NextToStateDurations()
    {
        var mappings = _resolver.ResolveAggregationMappings(
        [
            State("position", "0"),
            State("position", "100"),
            new AggregationColumn("position", AggregationFunction.Average)
        ]);

        mappings.Select(m => m.Wire).Should().Equal(
            "position_stateduration_0", "position_stateduration_100", "position_avg");
    }
}
