using System.Linq.Expressions;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements a GraphQL runtime entity type
/// </summary>
public sealed class RtRecordDtoInputType : InputObjectGraphType<RtRecordDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="ckRecordId">Corresponding construction kit id</param>
    public RtRecordDtoInputType(CkId<CkRecordId> ckRecordId)
    {
        CkRecordId = ckRecordId;
        Name = $"{ckRecordId.GetGraphQlName()}{CommonConstants.GraphQlInputSuffix}";
    }

    /// <summary>
    ///     Returns the construction kit id
    /// </summary>
    public CkId<CkRecordId> CkRecordId { get; }

    /// <inheritdoc />
    /// <remarks>We need an overload, to deserialize all properties to the dictionary of <see cref="RtEntityDto" /></remarks>
    public override object ParseDictionary(IDictionary<string, object?> value)
    {
        return value.ToObjectWithWithUnknownProperties<RtEntityDto>() ?? throw new InvalidOperationException();
    }

    /// <summary>
    ///     Populates the type with ck related attributes and associations
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="graphTypesCache"></param>
    /// <param name="recordGraph">The cache item</param>
    /// <param name="ckCacheService"></param>
    public void Populate(ICkCacheService ckCacheService, string tenantId, IGraphTypesCache graphTypesCache, CkRecordGraph recordGraph)
    {
        foreach (var attribute in recordGraph.AllAttributes.Values)
        {
            AddAttribute(graphTypesCache, attribute);
        }
    }

    private void AddAttribute(IGraphTypesCache graphTypesCache, CkTypeAttributeGraph attributeCacheItem)
    {
        var attributeName = attributeCacheItem.AttributeName;

        Expression<Func<RtRecordDto, object>> scalarValueExpression = dto => dto.Properties![attributeName];

        Expression<Func<RtRecordDto, ICollection<object>>> compoundValueExpression =
            dto => (ICollection<object>)dto.Properties![attributeName];

        switch (attributeCacheItem.ValueType)
        {
            case AttributeValueTypesDto.String:
                Field(attributeName, type: typeof(StringGraphType), expression: scalarValueExpression);
                break;
            case AttributeValueTypesDto.StringArray:
                Field(attributeName, type: typeof(ListGraphType<StringGraphType>), expression: compoundValueExpression);
                break;
            case AttributeValueTypesDto.Int:
                Field(attributeName, type: typeof(IntGraphType), expression: scalarValueExpression);
                break;
            case AttributeValueTypesDto.IntArray:
                Field(attributeName, type: typeof(ListGraphType<IntGraphType>), expression: compoundValueExpression);
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
                Field(attributeName, graphTypesCache.GetOrCreateInput(attributeCacheItem.ValueCkRecordId.Value));
                break;
            case AttributeValueTypesDto.RecordArray:
                if (attributeCacheItem.ValueCkRecordId == null)
                {
                    throw OctoGraphQLException.RecordAttributeHasNoCkRecordId(attributeCacheItem.AttributeName);
                }
                Field(attributeName, new ListGraphType(graphTypesCache.GetOrCreateInput(attributeCacheItem.ValueCkRecordId.Value)));
                break;            
            default:
                throw OctoGraphQLException.AttributeValueTypeNotSupported(attributeCacheItem.ValueType);
        }
    }
}
