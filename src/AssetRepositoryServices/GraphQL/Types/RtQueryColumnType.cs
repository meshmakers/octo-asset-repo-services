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
        // For aggregation columns the AttributePath is emitted in the engine's wire form
        // (path-concat + lowercase + `_funcsuffix`) so it lines up exactly with the cell key
        // — picker UIs can store the value verbatim, and MIN/MAX of the same source path
        // produce two distinct picker entries instead of two indistinguishable duplicates.
        // Simple-query columns keep the original CK path so they match the simple-query
        // cells, which still emit the original path.
        var path = aggregationType == AggregationTypesDto.None
            ? ckTypeQueryColumn.Path
            : RtAggregationCellKeyMapper.ToAggregationKey(ckTypeQueryColumn.Path, aggregationType);

        var rtQueryColumnDto = new RtQueryColumnDto
        {
            AttributePath = path,
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
    ///     Creates a simple column DTO for groupBy columns (no aggregation).
    ///     AttributePath is emitted in wire form so it lines up with the grouping cells
    ///     in the row payload.
    /// </summary>
    public static RtQueryColumnDto CreateGroupByColumnDto(CkTypeQueryColumn ckTypeQueryColumn)
    {
        var rtQueryColumnDto = new RtQueryColumnDto
        {
            AttributePath = RtAggregationCellKeyMapper.ToGroupingKey(ckTypeQueryColumn.Path),
            AttributeValueType = ckTypeQueryColumn.ValueType,
            AggregationType = AggregationTypesDto.None,
            UserContext = ckTypeQueryColumn
        };
        return rtQueryColumnDto;
    }
}