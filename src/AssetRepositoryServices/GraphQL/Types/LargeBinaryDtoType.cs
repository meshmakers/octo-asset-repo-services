using GraphQL.Types;
using GraphQLParser.AST;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Definition of a large binary content
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal class LargeBinaryDtoType : ScalarGraphType
{
    public LargeBinaryDtoType()
    {
        Name = "LargeBinary";
    }

    public override object? ParseLiteral(GraphQLValue value)
    {
        return value is GraphQLNullValue ? null : ThrowLiteralConversionError(value);
    }

    public override object? ParseValue(object? value)
    {
        return value switch
        {
            null => null,
            IFormFile => value,
            _ => ThrowValueConversionError(value)
        };
    }

    public override object? Serialize(object? value)
    {
        throw new InvalidOperationException("This scalar does not support serialization.");
    }
}