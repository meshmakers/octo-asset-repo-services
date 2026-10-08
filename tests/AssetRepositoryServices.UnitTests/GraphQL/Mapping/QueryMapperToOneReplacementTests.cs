using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Mapping;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL.Mapping;

/// <summary>
/// Query-row updates replace an existing edge only when the navigated side of the association role is
/// to-one. The decision must look at the multiplicity of the side matching the navigation direction.
/// </summary>
public class QueryMapperToOneReplacementTests
{
    [Theory]
    // Outbound navigation: only the outbound multiplicity matters.
    [InlineData(GraphDirections.Outbound, MultiplicitiesDto.N, MultiplicitiesDto.One, GraphDirections.Outbound)]
    [InlineData(GraphDirections.Outbound, MultiplicitiesDto.N, MultiplicitiesDto.ZeroOrOne, GraphDirections.Outbound)]
    [InlineData(GraphDirections.Outbound, MultiplicitiesDto.N, MultiplicitiesDto.N, null)]
    [InlineData(GraphDirections.Outbound, MultiplicitiesDto.One, MultiplicitiesDto.N, null)]
    [InlineData(GraphDirections.Outbound, MultiplicitiesDto.ZeroOrOne, MultiplicitiesDto.N, null)]
    // Inbound navigation: only the inbound multiplicity matters.
    [InlineData(GraphDirections.Inbound, MultiplicitiesDto.One, MultiplicitiesDto.N, GraphDirections.Inbound)]
    [InlineData(GraphDirections.Inbound, MultiplicitiesDto.ZeroOrOne, MultiplicitiesDto.N, GraphDirections.Inbound)]
    [InlineData(GraphDirections.Inbound, MultiplicitiesDto.N, MultiplicitiesDto.N, null)]
    // System/ParentChild (inbound N "Children", outbound One "Parent") navigated inbound: to-many, no replacement.
    [InlineData(GraphDirections.Inbound, MultiplicitiesDto.N, MultiplicitiesDto.One, null)]
    [InlineData(GraphDirections.Inbound, MultiplicitiesDto.N, MultiplicitiesDto.ZeroOrOne, null)]
    public void GetToOneReplacementDirection_UsesMultiplicityOfNavigatedSide(GraphDirections direction,
        MultiplicitiesDto inboundMultiplicity, MultiplicitiesDto outboundMultiplicity, GraphDirections? expected)
    {
        var result = QueryMapper.GetToOneReplacementDirection(direction, inboundMultiplicity, outboundMultiplicity);

        result.Should().Be(expected);
    }
}
