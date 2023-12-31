using System.Linq.Expressions;
using GraphQL;
using GraphQL.DataLoader;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements the GraphQL Runtime Entity Type
/// </summary>
public sealed class RtEntityDtoType : ObjectGraphType<RtEntityDto>
{
    /// <inheritdoc />
    public RtEntityDtoType(CkId<CkTypeId> ckTypeId)
    {
        CkTypeId = ckTypeId;

        Name = ckTypeId.GetGraphQlName();
        Description = $"Runtime entities of construction kit type '{ckTypeId}'";
        IsTypeOf = o =>
        {
            if (o is RtEntityDto rtEntityDto)
            {
                return rtEntityDto.CkTypeId == ckTypeId;
            }

            return false;
        };

        Field(d => d.RtId, type: typeof(OctoObjectIdType));
        Field(d => d.RtCreationDateTime, type: typeof(DateTimeGraphType));
        Field(d => d.RtChangedDateTime, type: typeof(DateTimeGraphType));
        Field(x => x.RtWellKnownName, true);
    }

    /// <summary>
    ///     Returns the Construction Kid Id of the object type
    /// </summary>
    public CkId<CkTypeId> CkTypeId { get; }


    internal void Populate(ICkCacheService ckCacheService, string tenantId, IGraphTypesCache entityDtoCache, IDataLoaderContextAccessor dataLoaderAccessor,
        IOctoSessionAccessor sessionAccessor, CkTypeGraph entityCacheItem)
    {
        AddConstructionKit(entityCacheItem);

        foreach (var attribute in entityCacheItem.AllAttributes.Values)
        {
            AddAttribute(attribute);
        }

        foreach (var ckTypeAssociationGraph in entityCacheItem.Associations.Out.All)
        {
            var allowedTypes = ckCacheService.GetCkType(tenantId, ckTypeAssociationGraph.TargetCkTypeId).DerivedTypes;
            if (!allowedTypes.Any())
            {
                continue; // All Ck entities are abstract for that associations
            }

            this.AssociationField(entityDtoCache, dataLoaderAccessor, sessionAccessor, ckTypeAssociationGraph.NavigationPropertyName,
                allowedTypes.Select(x => x.InheritorCkTypeId).Distinct().ToList(), entityCacheItem.CkTypeId,
                ckTypeAssociationGraph.CkRoleId, GraphDirections.Outbound);
        }

        foreach (var ckTypeAssociationGraph in entityCacheItem.Associations.In.All)
        {
            var allowedTypes = ckCacheService.GetCkType(tenantId, ckTypeAssociationGraph.TargetCkTypeId).DerivedTypes;
            if (!allowedTypes.Any())
            {
                continue; // All Ck entities are abstract for that associations
            }

            this.AssociationField(entityDtoCache, dataLoaderAccessor, sessionAccessor, ckTypeAssociationGraph.NavigationPropertyName,
                allowedTypes.Select(x => x.InheritorCkTypeId).Distinct().ToList(), entityCacheItem.CkTypeId,
                ckTypeAssociationGraph.CkRoleId, GraphDirections.Inbound);
        }
    }

    private void AddAttribute(CkTypeAttributeGraph attributeCacheItem)
    {
        // TODO: make better. is UserContext really needed? Maybe we can use Resolve method instead?
        Expression<Func<RtEntityDto, object?>> scalarValueExpression = dto =>
            ((RtEntity)dto.UserContext!).GetAttributeValueOrDefault(attributeCacheItem.AttributeName, null);

        Expression<Func<RtEntityDto, ICollection<object>?>> compoundValueExpression = dto =>
            ((RtEntity)dto.UserContext!).GetAttributeValueOrDefault(
                attributeCacheItem.AttributeName, null) as ICollection<object> ?? null;

        var attributeName = attributeCacheItem.AttributeName;
        switch (attributeCacheItem.ValueType)
        {
            case AttributeValueTypesDto.String:
                Field(attributeName, type: typeof(StringGraphType), expression: scalarValueExpression);
                break;
            case AttributeValueTypesDto.StringArray:
                Field(attributeName, type: typeof(ListGraphType<StringGraphType>),
                    expression: compoundValueExpression);
                break;
            case AttributeValueTypesDto.Int:
                Field(attributeName, type: typeof(IntGraphType), expression: scalarValueExpression);
                break;
            case AttributeValueTypesDto.IntArray:
                Field(attributeName, type: typeof(ListGraphType<IntGraphType>),
                    expression: compoundValueExpression);
                break;
            case AttributeValueTypesDto.Boolean:
                Field(attributeName, type: typeof(BooleanGraphType), expression: scalarValueExpression);
                break;
            case AttributeValueTypesDto.Double:
                Field(attributeName, type: typeof(DecimalGraphType), expression: scalarValueExpression);
                break;
            case AttributeValueTypesDto.DateTime:
                Field(attributeName, type: typeof(DateTimeGraphType), expression: scalarValueExpression);
                break;
            // case AttributeValueTypes.BinaryEmbedded:
            //     Field(attributeName, type: typeof(StringGraphType), expression: scalarValueExpression);
            //     break;
            case AttributeValueTypesDto.BinaryLinked:
                Field(attributeName, type: typeof(OctoObjectIdType), expression: scalarValueExpression);
                break;
            default:
                throw new NotImplementedException("Type is not supported for RT Entity GraphQL implementation");
        }
    }

    private void AddConstructionKit(CkTypeGraph ckTypeGraph)
    {
        Field<CkTypeDtoType>("ConstructionKitType")
            .Metadata(Statics.EntityCacheItem, ckTypeGraph)
            .Resolve(ResolveCkEntity);
    }

    private object ResolveCkEntity(IResolveFieldContext<RtEntityDto> arg)
    {
        // TODO: Fix save cast to CkTypeGraph
        var entityCacheItem = (CkTypeGraph)arg.FieldDefinition.Metadata[Statics.EntityCacheItem]!;
        return CkTypeDtoType.CreateCkTypeDto(entityCacheItem);
    }

    internal static RtEntityDto CreateRtEntityDto(RtEntity rtEntity)
    {
        var rtEntityDto = new RtEntityDto
        {
            RtId = rtEntity.RtId,
            CkTypeId = rtEntity.CkTypeId,
            RtCreationDateTime = rtEntity.RtCreationDateTime,
            RtChangedDateTime = rtEntity.RtChangedDateTime,
            RtWellKnownName = rtEntity.RtWellKnownName,
            UserContext = rtEntity
        };
        return rtEntityDto;
    }
}
