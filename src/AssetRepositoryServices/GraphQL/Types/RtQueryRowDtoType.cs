using AssetRepositoryServices.Resources;
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

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements a GraphQL runtime query row type for a runtime query
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RtQueryRowDtoType : ObjectGraphType<RtQueryRowDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public RtQueryRowDtoType()
    {
        Name = "RtQueryRow";
        Description = AssetTexts.Graphql_RtQueryRow_Description;
        Field(d => d.RtId, typeof(OctoObjectIdType));
        Field(d => d.CkTypeId, typeof(RtCkIdGraph<CkTypeId>));
        Field(x => x.RtCreationDateTime, true);
        Field(x => x.RtChangedDateTime, true);
        Field(x => x.RtWellKnownName, true);
        Field(x => x.RtVersion, true);

        Connection<NonNullGraphType<RtQueryCellDtoType>>("Cells")
            .Argument<ListGraphType<StringGraphType>>(Statics.AttributePathsFilterArg,
                AssetTexts.Graphql_Arguments_AttributePathsFilter_Description)
            .Resolve(ResolveCells);
    }

    private object ResolveCells(IResolveConnectionContext<RtQueryRowDto> context)
    {
        var ckCacheService = context.GetCkCacheService();
        var rtQueryRowUserContext = (RtQueryRowUserContext)context.Source.UserContext!;

        return ConnectionUtils.ToConnection(
            rtQueryRowUserContext.CkTypeQueryColumns.Select(item =>
                CreateRtQueryCellDto(ckCacheService, rtQueryRowUserContext.TenantId, rtQueryRowUserContext.RtEntity,
                    item)),
            context);
    }

    private RtQueryCellDto CreateRtQueryCellDto(ICkCacheService ckCacheService, string tenantId,
        RtEntityGraphItem rtEntity,
        CkTypeQueryColumn ckTypeQueryColumn)
    {
        var cellDto = new RtQueryCellDto
        {
            AttributePath = ckTypeQueryColumn.Path,
            Value = rtEntity.GetAttributeValueByAccessPath(ckCacheService, tenantId, ckTypeQueryColumn.AccessPathList,
                AttributeValueResolveFlags.ResolveEnumsToNames)
        };
        if (cellDto.Value is RtCkId<CkTypeId> ckTypeId)
        {
            cellDto.Value = ckTypeId.SemanticVersionedFullName;
        }

        return cellDto;
    }

    public static RtQueryRowDto CreateRtQueryRowDto(string tenantId, RtEntityGraphItem rtEntityGraphItem,
        IReadOnlyList<CkTypeQueryColumn> ckTypeQueryColumns)
    {
        var rtQueryRowDto = new RtQueryRowDto
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

internal class RtQueryRowUserContext(
    string tenantId,
    RtEntityGraphItem rtEntity,
    IReadOnlyList<CkTypeQueryColumn> ckTypeQueryColumns)
{
    public string TenantId { get; } = tenantId;

    public IReadOnlyList<CkTypeQueryColumn> CkTypeQueryColumns { get; } = ckTypeQueryColumns;

    public RtEntityGraphItem RtEntity { get; } = rtEntity;
}