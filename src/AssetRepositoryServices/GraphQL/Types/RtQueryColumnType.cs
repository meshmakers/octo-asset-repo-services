using AssetRepositoryServices.Resources;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements the GraphQL type for <see cref="RtQueryColumnDto" />.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RtQueryColumnType : ObjectGraphType<RtQueryColumnDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public RtQueryColumnType()
    {
        Name = "RtQueryColumn";
        Description = AssetTexts.Graphql_RtQueryColumn_Description;
        Field(d => d.AttributePath, typeof(StringGraphType));
        Field(d => d.AttributeValueType, typeof(AttributeValueTypesDtoType));
        Field(d => d.AggregationType, typeof(AggregationTypesDtoType));
    }

    public static RtQueryColumnDto CreateRtQueryColumnDto(CkTypeQueryColumn ckTypeQueryColumn, AggregationTypesDto aggregationType)
    {
        var rtQueryColumnDto = new RtQueryColumnDto
        {
            AttributePath = ckTypeQueryColumn.Path,
            AttributeValueType = GetAggregationResultType(ckTypeQueryColumn.ValueType, aggregationType),
            AggregationType = aggregationType,
            UserContext = ckTypeQueryColumn
        };
        return rtQueryColumnDto;
    }

    private static AttributeValueTypesDto GetAggregationResultType(
        AttributeValueTypesDto sourceType,
        AggregationTypesDto aggregationType)
    {
        return aggregationType switch
        {
            AggregationTypesDto.None => sourceType,
            AggregationTypesDto.Count => AttributeValueTypesDto.Integer,
            AggregationTypesDto.Sum => sourceType,
            AggregationTypesDto.Average => AttributeValueTypesDto.Double,
            AggregationTypesDto.Minimum => sourceType,
            AggregationTypesDto.Maximum => sourceType,
            _ => sourceType
        };
    }

    /// <summary>
    ///     Creates a simple column DTO for groupBy columns (no aggregation)
    /// </summary>
    public static RtQueryColumnDto CreateGroupByColumnDto(CkTypeQueryColumn ckTypeQueryColumn)
    {
        var rtQueryColumnDto = new RtQueryColumnDto
        {
            AttributePath = ckTypeQueryColumn.Path,
            AttributeValueType = ckTypeQueryColumn.ValueType,
            AggregationType = AggregationTypesDto.None,
            UserContext = ckTypeQueryColumn
        };
        return rtQueryColumnDto;
    }
}