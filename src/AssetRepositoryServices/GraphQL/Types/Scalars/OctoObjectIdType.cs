using GraphQL;
using GraphQL.Types;
using GraphQLParser.AST;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Converter;
using Meshmakers.Octo.ConstructionKit.Contracts;
using MongoDB.Bson;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;

/// <summary>
///     GraphQL type for Object Ids
/// </summary>
internal class OctoObjectIdType : ScalarGraphType
{
    /// <summary>
    /// Constructor
    /// </summary>
    public OctoObjectIdType()
    {
        Name = "OctoObjectId";
        Description = "A unique identifier for an runtime object.";
    }
    
    /// <inheritdoc />
    public override object? ParseLiteral(GraphQLValue value)
    {
        if (value is OctoObjectIdValue octoObjectIdValue)
        {
            return ParseValue(octoObjectIdValue.Value);
        }

        return value is GraphQLStringValue stringValue
            ? new OctoObjectId(stringValue.Value.ToString())
            : new OctoObjectId?();
    }

    /// <inheritdoc />
    public override object? ParseValue(object? value)
    {
        return ValueConverter.ConvertTo(value, typeof(OctoObjectId));
    }

    /// <inheritdoc />
    public override object? Serialize(object? value)
    {
        if (value is OctoObjectId octoObjectId)
        {
            return octoObjectId.ToString();
        }

        if (value is ObjectId objectId)
        {
            return objectId.ToString();
        }

        return value?.ToString();
    }
}