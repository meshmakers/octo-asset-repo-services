using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using GraphQL;
using GraphQL.DataLoader;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements the GraphQL Runtime Entity Type
/// </summary>
public sealed class RtEntityDtoType : ObjectGraphType<RtEntityDto>
{
    /// <inheritdoc />
    public RtEntityDtoType(string ckId)
    {
        CkId = ckId;

        Name = ckId.GetGraphQlName();
        Description = $"Runtime entities of construction kit type '{ckId}'";
        IsTypeOf = o =>
        {
            if (o is RtEntityDto rtEntityDto)
            {
                return rtEntityDto.CkId == ckId;
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
    public string CkId { get; }


    internal void Populate(IGraphTypesCache entityDtoCache, IDataLoaderContextAccessor dataLoaderAccessor,
        IOctoSessionAccessor sessionAccessor, EntityCacheItem entityCacheItem)
    {
        AddConstructionKit(entityCacheItem);

        foreach (var attribute in entityCacheItem.Attributes.Values)
        {
            AddAttribute(attribute);
        }

        foreach (var cacheItems in entityCacheItem.OutboundAssociations)
        {
            var allowedTypes = cacheItems.Value.SelectMany(x => x.AllowedTypes).ToList();
            var name = cacheItems.Key;
            if (!allowedTypes.Any())
            {
                continue; // All Ck entities are abstract for that associations
            }

            this.AssociationField(entityDtoCache, dataLoaderAccessor, sessionAccessor, name,
                allowedTypes.Select(x => x.CkId).Distinct().ToList(), entityCacheItem.CkId,
                cacheItems.Value.First().RoleId, GraphDirections.Outbound);
        }

        foreach (var cacheItems in entityCacheItem.InboundAssociations)
        {
            var allowedTypes = cacheItems.Value.SelectMany(x => x.AllowedTypes).ToList();
            var name = cacheItems.Key;
            if (!allowedTypes.Any())
            {
                continue; // All Ck entities are abstract for that associations
            }

            this.AssociationField(entityDtoCache, dataLoaderAccessor, sessionAccessor, name,
                allowedTypes.Select(x => x.CkId).Distinct().ToList(), entityCacheItem.CkId,
                cacheItems.Value.First().RoleId, GraphDirections.Inbound);
        }
    }

    private void AddAttribute(AttributeCacheItem attributeCacheItem)
    {
        Expression<Func<RtEntityDto, object>> scalarValueExpression = dto =>
            ((RtEntity)dto.UserContext).GetAttributeValueOrDefault(attributeCacheItem.AttributeName, null);

        Expression<Func<RtEntityDto, ICollection<object>>> compoundValueExpression = dto =>
            (ICollection<object>)((RtEntity)dto.UserContext).GetAttributeValueOrDefault(
                attributeCacheItem.AttributeName, null);

        var attributeName = attributeCacheItem.AttributeName;
        switch (attributeCacheItem.AttributeValueType)
        {
            case AttributeValueTypes.String:
                Field(attributeName, type: typeof(StringGraphType), expression: scalarValueExpression);
                break;
            case AttributeValueTypes.StringArray:
                Field(attributeName, type: typeof(ListGraphType<StringGraphType>),
                    expression: compoundValueExpression);
                break;
            case AttributeValueTypes.Int:
                Field(attributeName, type: typeof(IntGraphType), expression: scalarValueExpression);
                break;
            case AttributeValueTypes.IntArray:
                Field(attributeName, type: typeof(ListGraphType<IntGraphType>),
                    expression: compoundValueExpression);
                break;
            case AttributeValueTypes.Boolean:
                Field(attributeName, type: typeof(BooleanGraphType), expression: scalarValueExpression);
                break;
            case AttributeValueTypes.Double:
                Field(attributeName, type: typeof(DecimalGraphType), expression: scalarValueExpression);
                break;
            case AttributeValueTypes.DateTime:
                Field(attributeName, type: typeof(DateTimeGraphType), expression: scalarValueExpression);
                break;
            // case AttributeValueTypes.BinaryEmbedded:
            //     Field(attributeName, type: typeof(StringGraphType), expression: scalarValueExpression);
            //     break;
            case AttributeValueTypes.BinaryLinked:
                Field(attributeName, type: typeof(OctoObjectIdType), expression: scalarValueExpression);
                break;
            default:
                throw new NotImplementedException("Type is not supported for RT Entity GraphQL implementation");
        }
    }

    private void AddConstructionKit(EntityCacheItem entityCacheItem)
    {
        Field<CkEntityDtoType>("ConstructionKitType")
            .Metadata(Statics.EntityCacheItem, entityCacheItem)
            .Resolve(ResolveCkEntity);
    }

    private object ResolveCkEntity(IResolveFieldContext<RtEntityDto> arg)
    {
        var entityCacheItem = (EntityCacheItem)arg.FieldDefinition.Metadata[Statics.EntityCacheItem];
        return CkEntityDtoType.CreateCkEntityDto(entityCacheItem);
    }

    internal static RtEntityDto CreateRtEntityDto(RtEntity rtEntity)
    {
        var rtEntityDto = new RtEntityDto
        {
            RtId = rtEntity.RtId.ToOctoObjectId(),
            CkId = rtEntity.CkId,
            RtCreationDateTime = rtEntity.RtCreationDateTime,
            RtChangedDateTime = rtEntity.RtChangedDateTime,
            RtWellKnownName = rtEntity.RtWellKnownName,
            UserContext = rtEntity
        };
        return rtEntityDto;
    }
}
