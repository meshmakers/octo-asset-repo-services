using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Services.Common.Timeseries;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements the GraphQL Time series Entity Type
/// </summary>
internal sealed class TsEntityDtoType : ObjectGraphType<TsEntityDto>
{
    private readonly CkTypeGraph _ckTypeGraph;

    /// <inheritdoc />
    public TsEntityDtoType(CkTypeGraph ckTypeGraph)
    {
        _ckTypeGraph = ckTypeGraph;

        Name = _ckTypeGraph.CkTypeId.GetGraphQlPascalCaseNameForTs();
        Description = $"Time series entities of construction kit type '{_ckTypeGraph.CkTypeId}'";
        IsTypeOf = o =>
        {
            if (o is TsEntityDto rtEntityDto)
            {
                return _ckTypeGraph.GetAllDerivedTypes(true).Contains(rtEntityDto.CkTypeId);
            }

            return false;
        };

        Field(d => d.RtId, type: typeof(NonNullGraphType<OctoObjectIdType>));
        Field(d => d.CkTypeId, type: typeof(NonNullGraphType<CkIdTypeGraph<CkTypeId>>));
        Field(d => d.TimeStamp, type: typeof(DateTimeGraphType));
    }


    public CkId<CkTypeId> CkTypeId => _ckTypeGraph.CkTypeId;
    
    
    internal void Populate(IGraphTypesCache graphTypesCache)
    {
        AddConstructionKit();

        foreach (var attribute in _ckTypeGraph.AllAttributes.Values.Where(x=> x.IsDataStream))
        {
            Helpers.AddAttribute(this, graphTypesCache, attribute, false);
        }
    }

    private void AddConstructionKit()
    {
        Field<CkTypeDtoType>("ConstructionKitType")
            .Resolve(ResolveCkEntity);
    }
    
    private object ResolveCkEntity(IResolveFieldContext<TsEntityDto> arg)
    {
        var ckCacheService = arg.RequestServices?.GetRequiredService<ICkCacheService>();
        if (ckCacheService == null)
        {
            throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
        }
        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        var ckTypeGraph = ckCacheService.GetCkType(graphQlUserContext.TenantId, arg.Source.CkTypeId);
        return CkTypeDtoType.CreateCkTypeDto(ckTypeGraph);
    }

    internal static TsEntityDto CreateTsEntityDto(DataPointDto tsEntity)
    {
        var rtEntityDto = new TsEntityDto()
        {
            RtId = tsEntity.RtId ?? throw OctoGraphQLException.CkTypeIdUndefined(),
            CkTypeId = tsEntity.CkTypeId ?? throw OctoGraphQLException.CkTypeIdUndefined(),
            TimeStamp = tsEntity.Timestamp,
            UserContext = tsEntity
        };
        return rtEntityDto;
    }
}