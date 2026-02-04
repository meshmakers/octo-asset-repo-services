using GraphQL.Types;
using GraphQLParser.AST;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;

/// <summary>
///     Definition of a large binary content.
///     Supports multiple input formats:
///     - IFormFile for file uploads via multipart/form-data
///     - Base64 string (from JSON serialization of byte arrays)
///     - List of numbers (from raw JSON array like [137, 80, 78, 71, ...])
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
        return value switch
        {
            GraphQLNullValue => null,
            GraphQLStringValue stringValue => ParseBase64String(stringValue.Value.ToString()),
            GraphQLListValue listValue => ParseListLiteral(listValue),
            _ => ThrowLiteralConversionError(value)
        };
    }

    public override object? ParseValue(object? value)
    {
        return value switch
        {
            null => null,
            IFormFile => value,
            byte[] bytes => bytes,
            string base64String => ParseBase64String(base64String),
            IEnumerable<object> list => ParseObjectList(list),
            _ => ThrowValueConversionError(value)
        };
    }

    public override object? Serialize(object? value)
    {
        // Serialize byte array as base64 string
        return value switch
        {
            null => null,
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => throw new InvalidOperationException($"Cannot serialize value of type '{value.GetType().Name}' as LargeBinary.")
        };
    }

    /// <summary>
    /// Parses a base64 encoded string to byte array.
    /// </summary>
    private static byte[] ParseBase64String(string base64String)
    {
        if (string.IsNullOrEmpty(base64String))
        {
            return [];
        }

        try
        {
            return Convert.FromBase64String(base64String);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"Invalid base64 string for LargeBinary scalar: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Parses a list of objects (numbers) to byte array.
    /// Used when JSON arrays are passed as raw numbers, e.g., [137, 80, 78, 71, ...].
    /// </summary>
    private static byte[] ParseObjectList(IEnumerable<object> list)
    {
        return list.Select(item => Convert.ToByte(item)).ToArray();
    }

    /// <summary>
    /// Parses a GraphQL list literal to byte array.
    /// </summary>
    private static byte[] ParseListLiteral(GraphQLListValue listValue)
    {
        var bytes = new List<byte>();
        foreach (var item in listValue.Values ?? [])
        {
            if (item is GraphQLIntValue intValue)
            {
                bytes.Add(byte.Parse(intValue.Value.ToString()));
            }
            else
            {
                throw new InvalidOperationException(
                    $"Invalid value in LargeBinary list literal. Expected integer, got {item.GetType().Name}.");
            }
        }
        return bytes.ToArray();
    }
}