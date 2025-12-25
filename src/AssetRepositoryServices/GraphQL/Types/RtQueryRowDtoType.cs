using AssetRepositoryServices.Resources;
using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
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
            AttributePath = ckTypeQueryColumnTuple.Item1.Path,
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
