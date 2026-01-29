using System.Globalization;
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
        return ParseGraphQLValue(value);
    }

    /// <summary>
    /// Recursively parses a GraphQL AST value into a CLR object.
    /// Handles strings, integers, floats, booleans, lists, and objects (for nested records).
    /// </summary>
    private static object? ParseGraphQLValue(GraphQLValue value)
    {
        if (value is GraphQLNullValue)
        {
            return null;
        }

        if (value is GraphQLStringValue str)
        {
            return str.Value.ToString();
        }

        if (value is GraphQLIntValue intValue)
        {
            return int.Parse(intValue.Value);
        }

        if (value is GraphQLFloatValue floatValue)
        {
            return double.Parse(floatValue.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        if (value is GraphQLBooleanValue boolValue)
        {
            return boolValue.Value;
        }

        // Handle object values (for Record attributes with nested objects)
        if (value is GraphQLObjectValue objectValue)
        {
            return ParseObjectValue(objectValue);
        }

        // Handle list values (for RecordArray attributes or arrays of primitives)
        if (value is GraphQLListValue list)
        {
            return ParseListValue(list);
        }

        // Fallback for unknown types
        return null;
    }

    /// <summary>
    /// Parses a GraphQL object value into a Dictionary&lt;string, object&gt;.
    /// This is used for Record attributes where the value is an inline object literal.
    /// </summary>
    private static Dictionary<string, object?> ParseObjectValue(GraphQLObjectValue objectValue)
    {
        var result = new Dictionary<string, object?>();

        if (objectValue.Fields == null)
        {
            return result;
        }

        foreach (var field in objectValue.Fields)
        {
            var fieldName = field.Name.StringValue;
            var fieldValue = ParseGraphQLValue(field.Value);
            result[fieldName] = fieldValue;
        }

        return result;
    }

    /// <summary>
    /// Parses a GraphQL list value into a List&lt;object?&gt;.
    /// Handles nested objects and primitives within the list.
    /// </summary>
    private static List<object?> ParseListValue(GraphQLListValue list)
    {
        var items = new List<object?>();

        if (list.Values == null)
        {
            return items;
        }

        foreach (var item in list.Values)
        {
            items.Add(ParseGraphQLValue(item));
        }

        return items;
    }
}