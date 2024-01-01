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
///     Implements the GraphQL runtime record type
/// </summary>
public sealed class RtRecordDtoType : ObjectGraphType<RtRecordDto>
{
    /// <inheritdoc />
    public RtRecordDtoType(CkId<CkRecordId> ckRecordId)
    {
        CkRecordId = ckRecordId;

        Name = ckRecordId.GetGraphQlName();
        Description = $"Runtime entities of construction kit record '{ckRecordId}'";
        IsTypeOf = o =>
        {
            if (o is RtRecordDto rtRecordDto)
            {
                return rtRecordDto.CkRecordId == ckRecordId;
            }

            return false;
        };

    }

    /// <summary>
    ///     Returns the Construction Kid Id of the object type
    /// </summary>
    public CkId<CkRecordId> CkRecordId { get; }


    internal void Populate(ICkCacheService ckCacheService, string tenantId, IGraphTypesCache graphTypesCache, IDataLoaderContextAccessor dataLoaderAccessor,
        IOctoSessionAccessor sessionAccessor, CkRecordGraph entityCacheItem)
    {
        AddConstructionKit(entityCacheItem);

        foreach (var attribute in entityCacheItem.AllAttributes.Values)
        {
            AddAttribute(graphTypesCache, attribute);
        }
    }

    private void AddAttribute(IGraphTypesCache graphTypesCache,CkTypeAttributeGraph attributeCacheItem)
    {
        // TODO: make better. is UserContext really needed? Maybe we can use Resolve method instead?
        Expression<Func<RtRecordDto, object?>> scalarValueExpression = dto =>
            ((RtRecord)dto.UserContext!).GetAttributeValueOrDefault(attributeCacheItem.AttributeName, null);

        Expression<Func<RtRecordDto, ICollection<object>?>> compoundValueExpression = dto =>
            ((RtRecord)dto.UserContext!).GetAttributeValueOrDefault(
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
            case AttributeValueTypesDto.DateTimeOffset:
                Field(attributeName, type: typeof(DateTimeOffsetGraphType), expression: scalarValueExpression);
                break;
            case AttributeValueTypesDto.TimeSpan:
                Field(attributeName, type: typeof(TimeSpanSecondsGraphType), expression: scalarValueExpression);
                break;
            case AttributeValueTypesDto.Int64:
                Field(attributeName, type: typeof(LongGraphType), expression: scalarValueExpression);
                break;
            // case AttributeValueTypes.BinaryEmbedded:
            //     Field(attributeName, type: typeof(StringGraphType), expression: scalarValueExpression);
            //     break;
            case AttributeValueTypesDto.BinaryLinked:
                Field(attributeName, type: typeof(OctoObjectIdType), expression: scalarValueExpression);
                break;
            case AttributeValueTypesDto.Enum:
                Field(attributeName, type: typeof(IntGraphType), expression: scalarValueExpression);
                break;
            case AttributeValueTypesDto.Record:
                if (attributeCacheItem.ValueCkRecordId == null)
                {
                    throw OctoGraphQLException.RecordAttributeHasNoCkRecordId(attributeCacheItem.AttributeName);
                }
                Field(attributeName, graphTypesCache.GetOrCreate(attributeCacheItem.ValueCkRecordId.Value));
                break;
            case AttributeValueTypesDto.RecordArray:
                if (attributeCacheItem.ValueCkRecordId == null)
                {
                    throw OctoGraphQLException.RecordAttributeHasNoCkRecordId(attributeCacheItem.AttributeName);
                }
                Field(attributeName, new ListGraphType(graphTypesCache.GetOrCreate(attributeCacheItem.ValueCkRecordId.Value)));
                break;
            default:
                throw OctoGraphQLException.AttributeValueTypeNotSupported(attributeCacheItem.ValueType);
        }
    }

    private void AddConstructionKit(CkRecordGraph ckTypeGraph)
    {
        Field<CkTypeDtoType>("ConstructionKitType")
            .Metadata(Statics.EntityCacheItem, ckTypeGraph)
            .Resolve(ResolveCkEntity);
    }

    private object ResolveCkEntity(IResolveFieldContext<RtRecordDto> arg)
    {
        // TODO: Fix save cast to CkTypeGraph
        var entityCacheItem = (CkTypeGraph)arg.FieldDefinition.Metadata[Statics.EntityCacheItem]!;
        return CkTypeDtoType.CreateCkTypeDto(entityCacheItem);
    }

    internal static RtRecordDto CreateRtRecordDto(RtRecord rtEntity)
    {
        var rtRecordDto = new RtRecordDto
        {
            CkRecordId = rtEntity.CkRecordId,
            UserContext = rtEntity
        };
        return rtRecordDto;
    }
}
