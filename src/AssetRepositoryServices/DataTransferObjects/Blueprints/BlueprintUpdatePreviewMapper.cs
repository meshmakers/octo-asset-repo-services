using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer.DTOs;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Projects the engine's <see cref="BlueprintUpdatePreview" /> onto the wire DTO. Shared by the
///     REST controller and the GraphQL resolver so both surfaces carry the same picture (AB#5297,
///     AB#5308).
/// </summary>
/// <remarks>
///     Attribute values arrive in the engine's transport shape - scalars, <see cref="RtRecordTcDto" />,
///     lists of either. They are rendered to text here by hand, never through
///     <see cref="JsonSerializer" /> with default options: the transport DTOs carry converter
///     attributes that only resolve inside the engine's own serializer set, and serializing them
///     with anything else is exactly the 3.4.124 regression (AB#5297). Rendering by hand also keeps
///     the output stable for an operator: scalars verbatim, structures as compact JSON.
/// </remarks>
internal static class BlueprintUpdatePreviewMapper
{
    public static BlueprintUpdatePreviewDto ToDto(BlueprintUpdatePreview preview, string targetVersion)
    {
        return new BlueprintUpdatePreviewDto
        {
            TargetVersion = targetVersion,
            EntitiesToAdd = preview.EntitiesToAdd,
            EntitiesToUpdate = preview.EntitiesToUpdate,
            EntitiesUnchanged = preview.EntitiesUnchanged,
            EntitiesToDelete = preview.EntitiesToDelete,
            Conflicts = preview.Conflicts.Select(c => new BlueprintConflictDto
            {
                EntityId = c.EntityId,
                Description = c.Description,
                SuggestedResolution = c.SuggestedResolution.ToString()
            }).ToList(),
            Warnings = preview.Warnings.ToList(),
            Changes = preview.Changes.Select(ToDto).ToList()
        };
    }

    private static BlueprintEntityChangeDto ToDto(BlueprintEntityChange change)
    {
        return new BlueprintEntityChangeDto
        {
            EntityId = change.EntityId,
            EntityWellKnownName = change.EntityWellKnownName,
            EntityDisplayName = change.EntityDisplayName,
            EntityCkTypeId = change.EntityCkTypeId,
            Note = change.Note,
            Attributes = change.Attributes.Select(a => new BlueprintAttributeChangeDto
            {
                AttributeName = a.AttributeName,
                OldValue = FormatValue(a.OldValue),
                NewValue = FormatValue(a.NewValue)
            }).ToList()
        };
    }

    /// <summary>
    ///     Renders a transport-shaped attribute value for display. Scalars come back verbatim
    ///     (invariant culture, ISO-8601 for dates, base64 for binaries), records and lists as
    ///     compact JSON, anything unknown as its <c>ToString()</c>. A <see cref="JsonElement" />
    ///     follows the same split: a string token is a scalar and comes back unquoted like any other
    ///     string, every other token kind is emitted as its raw JSON. Never throws: a value that
    ///     cannot be rendered is not a reason to lose the whole preview.
    /// </summary>
    internal static string? FormatValue(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return s;
            case bool b:
                return b ? "true" : "false";
            case DateTime dt:
                return dt.ToString("o", CultureInfo.InvariantCulture);
            case DateTimeOffset dto:
                return dto.ToString("o", CultureInfo.InvariantCulture);
            case byte[] bytes:
                return Convert.ToBase64String(bytes);
            case JsonElement je:
                return je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText();
            case IFormattable f:
                return f.ToString(null, CultureInfo.InvariantCulture);
            case RtRecordTcDto:
            case IEnumerable:
                try
                {
                    using var stream = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(stream))
                    {
                        WriteStructure(writer, value);
                    }

                    return Encoding.UTF8.GetString(stream.ToArray());
                }
                catch (Exception)
                {
                    return value.ToString();
                }
            default:
                return value.ToString();
        }
    }

    private static void WriteStructure(Utf8JsonWriter writer, object? value)
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
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                // Every integral CLR width the engine may hand over stays a JSON number; decimal
                // holds all of them exactly (ulong.MaxValue < decimal.MaxValue).
                writer.WriteNumberValue(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
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
            case JsonElement je:
                je.WriteTo(writer);
                break;
            case byte[] bytes:
                // Binary CK attributes travel as byte[]; without this case IEnumerable would spell
                // them out byte by byte. Base64 is how JSON carries binaries anyway.
                writer.WriteBase64StringValue(bytes);
                break;
            case RtRecordTcDto record:
                writer.WriteStartObject();
                writer.WriteString("ckRecordId", record.CkRecordId?.ToString());
                foreach (var attribute in record.Attributes)
                {
                    writer.WritePropertyName(attribute.Id?.ToString() ?? string.Empty);
                    WriteStructure(writer, attribute.Value);
                }

                writer.WriteEndObject();
                break;
            case IEnumerable items:
                writer.WriteStartArray();
                foreach (var item in items)
                {
                    WriteStructure(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(FormatValue(value));
                break;
        }
    }
}
