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
            AttributeValueType = ckTypeQueryColumn.ValueType,
            AggregationType = aggregationType,
            UserContext = ckTypeQueryColumn
        };
        return rtQueryColumnDto;
    }

    /// <summary>
    ///     Creates a simple column DTO for groupBy columns (no aggregation)
    /// </summary>
    public static RtQueryColumnDto CreateGroupByColumnDto(string attributePath)
    {
        var rtQueryColumnDto = new RtQueryColumnDto
        {
            AttributePath = attributePath,
            // Use String as a default since the actual type is determined by the groupBy key values
            AttributeValueType = AttributeValueTypesDto.String,
            AggregationType = AggregationTypesDto.None
        };
        return rtQueryColumnDto;
    }
}