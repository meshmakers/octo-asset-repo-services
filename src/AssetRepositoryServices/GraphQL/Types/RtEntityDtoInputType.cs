using System.Linq.Expressions;
using GraphQL.Types;
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
public sealed class RtEntityDtoInputType : InputObjectGraphType<RtEntityDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="ckTypeId">Corresponding construction kit id</param>
    public RtEntityDtoInputType(CkId<CkTypeId> ckTypeId)
    {
        CkTypeId = ckTypeId;
        Name = $"{ckTypeId.GetGraphQlName()}{CommonConstants.GraphQlInputSuffix}";

        Field(x => x.RtWellKnownName, true);
    }

    /// <summary>
    ///     Returns the construction kit id
    /// </summary>
    public CkId<CkTypeId> CkTypeId { get; }

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
    /// <param name="entityCacheItem">The cache item</param>
    /// <param name="ckCacheService"></param>
    public void Populate(ICkCacheService ckCacheService, string tenantId, CkTypeGraph entityCacheItem)
    {
        foreach (var attribute in entityCacheItem.AllAttributes.Values)
        {
            AddAttribute(attribute);
        }

        foreach (var ckTypeAssociationGraph in entityCacheItem.Associations.Out.All)
        {
            var allowedTypes = ckCacheService.GetCkType(tenantId, ckTypeAssociationGraph.TargetCkTypeId).DerivedTypes;
            if (!allowedTypes.Any())
            {
                continue; // All Ck entities are abstract for that assocs
            }

            AddAssociation(ckTypeAssociationGraph.NavigationPropertyName);
        }

        foreach (var ckTypeAssociationGraph in entityCacheItem.Associations.In.All)
        {
            var allowedTypes = ckCacheService.GetCkType(tenantId, ckTypeAssociationGraph.TargetCkTypeId).DerivedTypes;
            if (!allowedTypes.Any())
            {
                continue; // All Ck entities are abstract for that assocs
            }

            AddAssociation(ckTypeAssociationGraph.NavigationPropertyName);
        }
    }

    private void AddAttribute(CkTypeAttributeGraph attributeCacheItem)
    {
        var attributeName = attributeCacheItem.AttributeName;

        Expression<Func<RtEntityDto, object>> scalarValueExpression = dto => dto.Properties![attributeName];

        Expression<Func<RtEntityDto, ICollection<object>>> compoundValueExpression =
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

    private void AddAssociation(string name)
    {
        Expression<Func<RtEntityDto, object>> scalarValueExpression = dto => dto.Properties![name];

        Field(name, type: typeof(ListGraphType<RtAssociationInputDtoType>), expression: scalarValueExpression);
    }
}
