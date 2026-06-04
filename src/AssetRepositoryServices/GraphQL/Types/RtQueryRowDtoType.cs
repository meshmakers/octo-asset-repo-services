using AssetRepositoryServices.Resources;
using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements a GraphQL runtime query row interface type for a runtime query
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RtQueryRowDtoType : InterfaceGraphType<IRtQueryRowDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public RtQueryRowDtoType()
    {
        Name = "RtQueryRow";
        Description = AssetTexts.Graphql_RtQueryRow_Description;
        Field(d => d.CkTypeId, typeof(RtCkIdGraph<CkTypeId>));
        Connection<NonNullGraphType<RtQueryCellDtoType>>("Cells")
            .Argument<ListGraphType<StringGraphType>>(Statics.AttributePathsFilterArg,
                AssetTexts.Graphql_Arguments_AttributePathsFilter_Description)
            .Argument<BooleanGraphType>(Statics.ResolveEnumValuesToNames,
                "When true, enum integer values are resolved to their label names. Defaults to true.");
    }
}

/// <summary>
///     Implements a GraphQL runtime simple query row type for a runtime query
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RtSimpleQueryRowDtoType : ObjectGraphType<RtSimpleQueryRowDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public RtSimpleQueryRowDtoType()
    {
        Name = "RtSimpleQueryRow";
        Description = AssetTexts.Graphql_RtQueryRow_Description;

        Interface<RtQueryRowDtoType>();

        Field(d => d.RtId, typeof(OctoObjectIdType));
        Field(d => d.CkTypeId, typeof(RtCkIdGraph<CkTypeId>));
        Field(x => x.RtCreationDateTime, true);
        Field(x => x.RtChangedDateTime, true);
        Field(x => x.RtWellKnownName, true);
        Field(x => x.RtVersion, true);

        Connection<NonNullGraphType<RtQueryCellDtoType>>("Cells")
            .Argument<ListGraphType<StringGraphType>>(Statics.AttributePathsFilterArg,
                AssetTexts.Graphql_Arguments_AttributePathsFilter_Description)
            .Argument<BooleanGraphType>(Statics.ResolveEnumValuesToNames,
                "When true, enum integer values are resolved to their label names. Defaults to true.")
            .Resolve(ResolveCells);
    }

    private object ResolveCells(IResolveConnectionContext<RtSimpleQueryRowDto> context)
    {
        var ckCacheService = context.GetCkCacheService();

        if (context.Source.UserContext is RtQueryRowUserContext rtQueryRowUserContext)
        {
            // Default to true for backward compatibility with existing behavior
            context.TryGetArgument(Statics.ResolveEnumValuesToNames, true, out bool resolveEnumValuesToNames);

            return ConnectionUtils.ToOctoConnection(
                rtQueryRowUserContext.CkTypeQueryColumns.Select(item =>
                    CreateRtSimpleQueryCellDto(ckCacheService, rtQueryRowUserContext.TenantId,
                        rtQueryRowUserContext.RtEntity,
                        item.Item1, resolveEnumValuesToNames)),
                context);
        }

        throw OctoGraphQLException.UnknownUserContextType();
    }

    private RtQueryCellDto CreateRtSimpleQueryCellDto(ICkCacheService ckCacheService, string tenantId,
        RtEntityGraphItem rtEntity,
        CkTypeQueryColumn ckTypeQueryColumn, bool resolveEnumValuesToNames)
    {
        // For N:M associations, return totalCount or exists based on path suffix
        if (ckTypeQueryColumn.AssociationTuple is { Multiplicity: MultiplicitiesDto.N })
        {
            var accessPathList = ckTypeQueryColumn.AccessPathList.ToArray();
            var navigationTerm = accessPathList.FirstOrDefault(p => p.Type == PathType.Navigation);
            var count = 0;
            if (navigationTerm != null)
            {
                count = rtEntity.Associations
                    .Count(a => a.NavigationPropertyName == navigationTerm.Value);
            }

            var isTotalCount = ckTypeQueryColumn.Path.EndsWith("totalCount");
            return new RtQueryCellDto
            {
                AttributePath = ckTypeQueryColumn.Path,
                Value = isTotalCount ? count : count > 0
            };
        }

        var resolveFlags = resolveEnumValuesToNames
            ? AttributeValueResolveFlags.ResolveEnumsToNames
            : AttributeValueResolveFlags.Default;

        var cellDto = new RtQueryCellDto
        {
            AttributePath = ckTypeQueryColumn.Path,
            Value = rtEntity.GetAttributeValueByAccessPath(ckCacheService, tenantId, ckTypeQueryColumn.AccessPathList,
                resolveFlags)
        };
        if (cellDto.Value is RtCkId<CkTypeId> ckTypeId)
        {
            cellDto.Value = ckTypeId.SemanticVersionedFullName;
        }

        return cellDto;
    }

    public static RtSimpleQueryRowDto CreateRtQueryRowDto(string tenantId, RtEntityGraphItem rtEntityGraphItem,
        IReadOnlyList<Tuple<CkTypeQueryColumn, AggregationTypesDto>> ckTypeQueryColumns)
    {
        var rtQueryRowDto = new RtSimpleQueryRowDto
        {
            RtId = rtEntityGraphItem.RtId,
            CkTypeId = rtEntityGraphItem.CkTypeId ?? throw OctoGraphQLException.CkTypeIdUndefined(),
            RtCreationDateTime = rtEntityGraphItem.RtCreationDateTime,
            RtChangedDateTime = rtEntityGraphItem.RtChangedDateTime,
            RtWellKnownName = rtEntityGraphItem.RtWellKnownName,
            RtVersion = rtEntityGraphItem.RtVersion,
            UserContext = new RtQueryRowUserContext(tenantId, rtEntityGraphItem, ckTypeQueryColumns)
        };

        return rtQueryRowDto;
    }
}

/// <summary>
///     Implements a GraphQL runtime aggregation query row type for a runtime query
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RtAggregationQueryRowDtoType : ObjectGraphType<RtAggregationQueryRowDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public RtAggregationQueryRowDtoType()
    {
        Name = "RtAggregationQueryRow";
        Description = AssetTexts.Graphql_RtQueryRow_Description;

        Interface<RtQueryRowDtoType>();

        Field(d => d.CkTypeId, typeof(RtCkIdGraph<CkTypeId>));

        Connection<NonNullGraphType<RtQueryCellDtoType>>("Cells")
            .Argument<ListGraphType<StringGraphType>>(Statics.AttributePathsFilterArg,
                AssetTexts.Graphql_Arguments_AttributePathsFilter_Description)
            .Argument<BooleanGraphType>(Statics.ResolveEnumValuesToNames,
                "When true, enum integer values are resolved to their label names. Defaults to true.")
            .Resolve(ResolveCells);
    }

    private object ResolveCells(IResolveConnectionContext<RtAggregationQueryRowDto> context)
    {
        context.GetCkCacheService();

        if (context.Source.UserContext is RtAggregatedQueryRowUserContext rtAggregatedQueryRowUserContext)
        {
            return ConnectionUtils.ToConnection(
                rtAggregatedQueryRowUserContext.CkTypeQueryColumns.Select(item =>
                    CreateRtAggregatedQueryCellDto(rtAggregatedQueryRowUserContext.ResultSetAggregationResult,
                        item)),
                context);
        }

        throw OctoGraphQLException.UnknownUserContextType();
    }

    private RtQueryCellDto CreateRtAggregatedQueryCellDto(AggregationResult resultSetAggregationResult,
        Tuple<CkTypeQueryColumn, AggregationTypesDto> ckTypeQueryColumnTuple)
    {
        object? value;
        switch (ckTypeQueryColumnTuple.Item2)
        {
            case AggregationTypesDto.Count:
                value = resultSetAggregationResult.CountStatistics.FirstOrDefault(a=> a.AttributePath == ckTypeQueryColumnTuple.Item1.Path)?.Value;
                break;
            case AggregationTypesDto.Sum:
                value = resultSetAggregationResult.SumStatistics.FirstOrDefault(a=> a.AttributePath == ckTypeQueryColumnTuple.Item1.Path)?.Value;
                break;
            case AggregationTypesDto.Average:
                value = resultSetAggregationResult.AvgStatistics.FirstOrDefault(a=> a.AttributePath == ckTypeQueryColumnTuple.Item1.Path)?.Value;
                break;
            case AggregationTypesDto.Minimum:
                value = resultSetAggregationResult.MinStatistics.FirstOrDefault(a=> a.AttributePath == ckTypeQueryColumnTuple.Item1.Path)?.Value;
                break;
            case AggregationTypesDto.Maximum:
                value = resultSetAggregationResult.MaxStatistics.FirstOrDefault(a=> a.AttributePath == ckTypeQueryColumnTuple.Item1.Path)?.Value;
                break;
            default:
                throw OctoGraphQLException.UnknownUserContextType();
        }

        var cellDto = new RtQueryCellDto
        {
            AttributePath = RtAggregationCellKeyMapper.ToAggregationKey(
                ckTypeQueryColumnTuple.Item1.Path, ckTypeQueryColumnTuple.Item2),
            Value = value
        };

        return cellDto;
    }

    public static RtAggregationQueryRowDto CreateRtQueryRowDto(string tenantId, RtCkId<CkTypeId> queryCkTypeId, AggregationResult resultSetAggregationResult, IReadOnlyList<Tuple<CkTypeQueryColumn, AggregationTypesDto>> ckTypeQueryColumns)
    {
        var rtQueryRowDto = new RtAggregationQueryRowDto
        {
            CkTypeId = queryCkTypeId,
            UserContext = new RtAggregatedQueryRowUserContext(tenantId, resultSetAggregationResult, ckTypeQueryColumns)
        };

        return rtQueryRowDto;
    }
}

internal class RtQueryRowUserContext(
    string tenantId,
    RtEntityGraphItem rtEntity,
    IReadOnlyList<Tuple<CkTypeQueryColumn, AggregationTypesDto>> ckTypeQueryColumns)
{
    public string TenantId { get; } = tenantId;

    public IReadOnlyList<Tuple<CkTypeQueryColumn, AggregationTypesDto>> CkTypeQueryColumns { get; } = ckTypeQueryColumns;

    public RtEntityGraphItem RtEntity { get; } = rtEntity;
}

internal class RtAggregatedQueryRowUserContext(
    string tenantId,
    AggregationResult resultSetAggregationResult,
    IReadOnlyList<Tuple<CkTypeQueryColumn, AggregationTypesDto>> ckTypeQueryColumns)
{
    public string TenantId { get; } = tenantId;

    public IReadOnlyList<Tuple<CkTypeQueryColumn, AggregationTypesDto>> CkTypeQueryColumns { get; } = ckTypeQueryColumns;

    public AggregationResult ResultSetAggregationResult { get; } = resultSetAggregationResult;
}



/// <summary>
///     Implements a GraphQL runtime grouping aggregation query row type for a runtime query
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RtGroupingAggregationQueryRowDtoType : ObjectGraphType<RtGroupingAggregationQueryRowDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public RtGroupingAggregationQueryRowDtoType()
    {
        Name = "RtGroupingAggregationQueryRow";
        Description = AssetTexts.Graphql_RtQueryRow_Description;

        Interface<RtQueryRowDtoType>();

        Field(d => d.CkTypeId, typeof(RtCkIdGraph<CkTypeId>));

        Connection<NonNullGraphType<RtQueryCellDtoType>>("Cells")
            .Argument<ListGraphType<StringGraphType>>(Statics.AttributePathsFilterArg,
                AssetTexts.Graphql_Arguments_AttributePathsFilter_Description)
            .Argument<BooleanGraphType>(Statics.ResolveEnumValuesToNames,
                "When true, enum integer values are resolved to their label names. Defaults to true.")
            .Resolve(ResolveCells);
    }

    private object ResolveCells(IResolveConnectionContext<RtGroupingAggregationQueryRowDto> context)
    {
        if (context.Source.UserContext is RtGroupingAggregatedQueryRowUserContext groupingContext)
        {
            var cells = new List<RtQueryCellDto>();

            // First, add cells for the GroupBy columns with their key values.
            // AttributePath is emitted in the same wire form as aggregation cells
            // (concat-segments + lowercase, no function suffix) so the frontend can
            // address grouping and aggregation cells with one consistent key convention.
            var groupByPaths = groupingContext.FieldAggregationResult.GroupByAttributePaths.ToList();
            var keys = groupingContext.FieldAggregationResult.Keys.ToList();

            for (var i = 0; i < groupByPaths.Count; i++)
            {
                cells.Add(new RtQueryCellDto
                {
                    AttributePath = RtAggregationCellKeyMapper.ToGroupingKey(groupByPaths[i]),
                    Value = i < keys.Count ? keys[i] : null
                });
            }

            // Then, add cells for the aggregation columns
            cells.AddRange(groupingContext.CkTypeQueryColumns.Select(item =>
                CreateRtGroupingAggregatedQueryCellDto(groupingContext.FieldAggregationResult, item)));

            return ConnectionUtils.ToConnection(cells, context);
        }

        throw OctoGraphQLException.UnknownUserContextType();
    }

    private RtQueryCellDto CreateRtGroupingAggregatedQueryCellDto(FieldAggregationResult fieldAggregationResult,
        Tuple<CkTypeQueryColumn, AggregationTypesDto> ckTypeQueryColumnTuple)
    {
        object? value;
        switch (ckTypeQueryColumnTuple.Item2)
        {
            case AggregationTypesDto.Count:
                value = fieldAggregationResult.CountStatistics.FirstOrDefault(a => a.AttributePath == ckTypeQueryColumnTuple.Item1.Path)?.Value;
                break;
            case AggregationTypesDto.Sum:
                value = fieldAggregationResult.SumStatistics.FirstOrDefault(a => a.AttributePath == ckTypeQueryColumnTuple.Item1.Path)?.Value;
                break;
            case AggregationTypesDto.Average:
                value = fieldAggregationResult.AvgStatistics.FirstOrDefault(a => a.AttributePath == ckTypeQueryColumnTuple.Item1.Path)?.Value;
                break;
            case AggregationTypesDto.Minimum:
                value = fieldAggregationResult.MinStatistics.FirstOrDefault(a => a.AttributePath == ckTypeQueryColumnTuple.Item1.Path)?.Value;
                break;
            case AggregationTypesDto.Maximum:
                value = fieldAggregationResult.MaxStatistics.FirstOrDefault(a => a.AttributePath == ckTypeQueryColumnTuple.Item1.Path)?.Value;
                break;
            default:
                throw OctoGraphQLException.UnknownUserContextType();
        }

        var cellDto = new RtQueryCellDto
        {
            AttributePath = RtAggregationCellKeyMapper.ToAggregationKey(
                ckTypeQueryColumnTuple.Item1.Path, ckTypeQueryColumnTuple.Item2),
            Value = value
        };

        return cellDto;
    }

    public static RtGroupingAggregationQueryRowDto CreateRtQueryRowDto(string tenantId, RtCkId<CkTypeId> queryCkTypeId,
        FieldAggregationResult fieldAggregationResult, IReadOnlyList<Tuple<CkTypeQueryColumn, AggregationTypesDto>> ckTypeQueryColumns)
    {
        var rtQueryRowDto = new RtGroupingAggregationQueryRowDto
        {
            CkTypeId = queryCkTypeId,
            UserContext = new RtGroupingAggregatedQueryRowUserContext(tenantId, fieldAggregationResult, ckTypeQueryColumns)
        };

        return rtQueryRowDto;
    }
}

internal class RtGroupingAggregatedQueryRowUserContext(
    string tenantId,
    FieldAggregationResult fieldAggregationResult,
    IReadOnlyList<Tuple<CkTypeQueryColumn, AggregationTypesDto>> ckTypeQueryColumns)
{
    public string TenantId { get; } = tenantId;

    public IReadOnlyList<Tuple<CkTypeQueryColumn, AggregationTypesDto>> CkTypeQueryColumns { get; } = ckTypeQueryColumns;

    public FieldAggregationResult FieldAggregationResult { get; } = fieldAggregationResult;
}

/// <summary>
/// Wire-format key generator for aggregation/grouping-aggregation result cells.
/// Mirrors the convention used by the stream-data side (CrateDB-backed) so that
/// frontend code can address cells from both worlds with one rule:
/// <list type="bullet">
///   <item>Aggregation cell key: <c>{pathConcatLower}_{functionSuffix}</c>, e.g. <c>amountvalue_sum</c>.</item>
///   <item>Grouping cell key: <c>{pathConcatLower}</c>, e.g. <c>operatingstatus</c>.</item>
/// </list>
/// The function suffix is required so multiple aggregations on the same attribute
/// path (MIN + MAX of <c>amount.value</c>) produce distinct keys instead of colliding
/// on the same row entry.
/// </summary>
internal static class RtAggregationCellKeyMapper
{
    public static string ToAggregationKey(string path, AggregationTypesDto type)
        => $"{PathToColumnName(path)}_{FunctionSuffix(type)}";

    public static string ToGroupingKey(string path) => PathToColumnName(path);

    private static string PathToColumnName(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(path.Length);
        foreach (var segment in path.Split('.'))
        {
            sb.Append(segment);
        }
        return sb.ToString().ToLowerInvariant();
    }

    private static string FunctionSuffix(AggregationTypesDto type) => type switch
    {
        AggregationTypesDto.Count => "count",
        AggregationTypesDto.Sum => "sum",
        AggregationTypesDto.Average => "avg",
        AggregationTypesDto.Minimum => "min",
        AggregationTypesDto.Maximum => "max",
        _ => type.ToString().ToLowerInvariant()
    };
}
