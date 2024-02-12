using GraphQL.Types;
using GraphQLParser.AST;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;

internal class SimpleScalarType : ScalarGraphType
{
    public override object? Serialize(object? value)
    {
        return value;
    }

    public override object? ParseValue(object? value)
    {
        return value;
    }

    public override object? ParseLiteral(GraphQLValue value)
    {
        if (value is GraphQLStringValue str)
        {
            return str.Value.ToString();
        }

        return null;
    }
}