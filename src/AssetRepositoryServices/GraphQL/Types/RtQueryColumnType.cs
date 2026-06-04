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
        // For aggregation columns the AttributePath is projected to the engine's wire
        // form (`path-concat-lower + _funcsuffix`) so it lines up exactly with the
        // aggregation cell key — picker UIs can store it verbatim, and MIN/MAX of the
        // same source path produce two distinct entries instead of two duplicates.
        // Grouping and simple-query columns keep their original CK path so internal
        // resolvers can still tokenize navigation properties; the frontend matcher
        // reconciles those against the wire-form grouping cells via its loose-fallback.
        Field<StringGraphType>("AttributePath")
            .Resolve(context =>
            {
                var dto = context.Source;
                return dto.AggregationType == AggregationTypesDto.None
                    ? dto.AttributePath
                    : RtAggregationCellKeyMapper.ToAggregationKey(dto.AttributePath ?? string.Empty, dto.AggregationType);
            });
        Field(d => d.AttributeValueType, typeof(AttributeValueTypesDtoType));
        Field(d => d.AggregationType, typeof(AggregationTypesDtoType));
    }

    public static RtQueryColumnDto CreateRtQueryColumnDto(CkTypeQueryColumn ckTypeQueryColumn, AggregationTypesDto aggregationType)
    {
        // AttributePath stays in the original CK form on the DTO — internal resolvers
        // (path tokenizer, navigation pair lookup, aggregation result join) parse it
        // as a CK attribute path, and collapsing the segments there would break
        // navigation properties. The wire-form key (with `_funcsuffix` for aggregations)
        // is projected only at the GraphQL response layer, in RtQueryColumnType.
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
    ///     Creates a simple column DTO for groupBy columns (no aggregation). Keeps the
    ///     original CK path on the DTO so internal resolvers can still tokenize the path
    ///     for navigation lookups. The GraphQL output stays in the original form too —
    ///     grouping columns are typed paths the operator picked and don't need to be
    ///     disambiguated against sibling aggregations.
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