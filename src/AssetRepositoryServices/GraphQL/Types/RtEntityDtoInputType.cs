using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using GraphQL.Types;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements a GraphQL runtime entity type
/// </summary>
public class RtEntityDtoInputType : InputObjectGraphType<RtEntityDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="ckId">Corresponding construction kit id</param>
    public RtEntityDtoInputType(string ckId)
    {
        CkId = ckId;
        Name = $"{ckId.GetGraphQlName()}{CommonConstants.GraphQlInputSuffix}";

        Field(x => x.RtWellKnownName, true);
    }

    /// <summary>
    ///     Returns the construction kit id
    /// </summary>
    public string CkId { get; }

    /// <inheritdoc />
    /// <remarks>We need an overload, to deserialize all properties to the dictionary of <see cref="RtEntityDto" /></remarks>
    public override object ParseDictionary(IDictionary<string, object> value)
    {
        return value.ToObjectWithWithUnknownProperties<RtEntityDto>();
    }

    /// <summary>
    ///     Populates the type with ck related attributes and associations
    /// </summary>
    /// <param name="entityCacheItem">The cache item</param>
    public void Populate(EntityCacheItem entityCacheItem)
    {
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
                continue; // All Ck entities are abstract for that assocs
            }

            AddAssociation(name);
        }

        foreach (var cacheItems in entityCacheItem.InboundAssociations)
        {
            var allowedTypes = cacheItems.Value.SelectMany(x => x.AllowedTypes).ToList();
            var name = cacheItems.Key;
            if (!allowedTypes.Any())
            {
                continue; // All Ck entities are abstract for that assocs
            }

            AddAssociation(name);
        }
    }

    private void AddAttribute(AttributeCacheItem attributeCacheItem)
    {
        var attributeName = attributeCacheItem.AttributeName;

        Expression<Func<RtEntityDto, object>> scalarValueExpression = dto => dto.Properties[attributeName];

        Expression<Func<RtEntityDto, ICollection<object>>> compoundValueExpression =
            dto => (ICollection<object>)dto.Properties[attributeName];

        switch (attributeCacheItem.AttributeValueType)
        {
            case AttributeValueTypes.String:
                Field(attributeName, type: typeof(StringGraphType), expression: scalarValueExpression);
                break;
            case AttributeValueTypes.StringArray:
                Field(attributeName, type: typeof(ListGraphType<StringGraphType>), expression: compoundValueExpression);
                break;
            case AttributeValueTypes.Int:
                Field(attributeName, type: typeof(IntGraphType), expression: scalarValueExpression);
                break;
            case AttributeValueTypes.IntArray:
                Field(attributeName, type: typeof(ListGraphType<IntGraphType>), expression: compoundValueExpression);
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

    private void AddAssociation(string name)
    {
        Expression<Func<RtEntityDto, object>> scalarValueExpression = dto => dto.Properties[name];

        Field(name, type: typeof(ListGraphType<RtAssociationInputDtoType>), expression: scalarValueExpression);
    }
}
