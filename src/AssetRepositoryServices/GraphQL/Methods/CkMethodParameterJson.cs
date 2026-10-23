using System.Collections;
using System.Globalization;
using System.Text.Json;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;

/// <summary>
///     AB#7026: turns the <c>input</c> argument of a method mutation (the dictionary GraphQL.NET parsed from the
///     input object, scalars already converted by the scalar types) into the parameters JSON object of
///     <see cref="CkMethodGatewayCall.ParametersJson" />. The member names are the declared parameter names; the
///     handler reads them case-insensitively. Values follow the wire form of <c>CkMethodJson</c>: enums as their
///     integer key (accepted next to the member name), dates as ISO 8601, time spans in the invariant <c>c</c> format,
///     binaries as base64, records as objects of their attributes. Nothing here logs or names a value; a conversion
///     failure carries the value type only.
/// </summary>
internal static class CkMethodParameterJson
{
    public static string ToJson(IDictionary<string, object?>? input)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (input != null)
            {
                foreach (var (name, value) in input)
                {
                    writer.WritePropertyName(name);
                    Write(writer, value);
                }
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case short sh:
                writer.WriteNumberValue(sh);
                break;
            case byte by:
                writer.WriteNumberValue(by);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            case DateTime dt:
                writer.WriteStringValue(dt.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                    : dt.ToUniversalTime());
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto);
                break;
            case TimeSpan ts:
                writer.WriteStringValue(ts.ToString("c", CultureInfo.InvariantCulture));
                break;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;
            case RtRecordDto record:
                writer.WriteStartObject();
                foreach (var attribute in record.Attributes ?? [])
                {
                    writer.WritePropertyName(attribute.AttributeName);
                    Write(writer, attribute.Value);
                }

                writer.WriteEndObject();
                break;
            case IDictionary<string, object?> dictionary:
                writer.WriteStartObject();
                foreach (var (key, item) in dictionary)
                {
                    writer.WritePropertyName(key);
                    Write(writer, item);
                }

                writer.WriteEndObject();
                break;
            case IEnumerable<byte> byteList:
                writer.WriteBase64StringValue(byteList.ToArray());
                break;
            case IEnumerable list:
                writer.WriteStartArray();
                foreach (var item in list)
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            case Enum e:
                writer.WriteNumberValue(System.Convert.ToInt32(e, CultureInfo.InvariantCulture));
                break;
            default:
                // An id scalar (OctoObjectId and the like) travels as its string form.
                writer.WriteStringValue(System.Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }
}
