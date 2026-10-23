using System.Globalization;
using System.Text.Json;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;

/// <summary>
///     AB#5670: converts the JSON result of a handler (<c>CkMethodResponse.ResultJson</c>, the camelCase wire form of
///     <c>CkMethodJson</c>) into the value the generated result field serves. The conversion follows the declared
///     value type of the method result, never the shape of the JSON.
/// </summary>
internal static class CkMethodResultConverter
{
    /// <summary>
    ///     Converts <paramref name="json" /> for the declared result. Throws <see cref="InvalidOperationException" /> (never
    ///     with the value in the message) when the JSON does not fit the declared type.
    /// </summary>
    public static object? ToResultValue(ICkCacheService ckCacheService, string tenantId, CkMethodResultDto declared,
        JsonElement json)
    {
        if (json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var value = Convert(ckCacheService, tenantId, declared.ValueType, declared.ValueCkEnumId,
            declared.ValueCkRecordId, json, inRecord: false);
        return value switch
        {
            RtRecord record => RtRecordDtoType.CreateRtRecordDto(record),
            List<RtRecord> records => records.Select(RtRecordDtoType.CreateRtRecordDto).ToList(),
            _ => value
        };
    }

    private static object? Convert(ICkCacheService ckCacheService, string tenantId, AttributeValueTypesDto valueType,
        CkId<CkEnumId>? enumId, CkId<CkRecordId>? recordId, JsonElement json, bool inRecord)
    {
        if (json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        try
        {
            switch (valueType)
            {
                case AttributeValueTypesDto.String:
                    return json.ValueKind == JsonValueKind.String ? json.GetString() : json.GetRawText();
                case AttributeValueTypesDto.StringArray:
                    return Array(json, e => e.GetString()!);
                case AttributeValueTypesDto.Int:
                    return json.GetInt32();
                case AttributeValueTypesDto.IntArray:
                    return Array(json, e => e.GetInt32());
                case AttributeValueTypesDto.Boolean:
                    return json.GetBoolean();
                case AttributeValueTypesDto.Double:
                    // The runtime holds doubles; the result field is a decimal scalar.
                    return inRecord ? json.GetDouble() : json.GetDecimal();
                case AttributeValueTypesDto.Int64:
                    return json.GetInt64();
                case AttributeValueTypesDto.DateTime:
                    return DateTime.SpecifyKind(json.GetDateTime().ToUniversalTime(), DateTimeKind.Utc);
                case AttributeValueTypesDto.DateTimeOffset:
                    return json.GetDateTimeOffset();
                case AttributeValueTypesDto.TimeSpan:
                    return json.ValueKind == JsonValueKind.Number
                        ? TimeSpan.FromSeconds(json.GetDouble())
                        : TimeSpan.Parse(json.GetString()!, CultureInfo.InvariantCulture);
                case AttributeValueTypesDto.Binary:
                    return json.GetBytesFromBase64();
                case AttributeValueTypesDto.Enum:
                    return EnumKey(ckCacheService, tenantId, enumId, json);
                case AttributeValueTypesDto.Record:
                    return ToRecord(ckCacheService, tenantId, recordId, json);
                case AttributeValueTypesDto.RecordArray:
                    return Array(json, e => ToRecord(ckCacheService, tenantId, recordId, e));
                default:
                    throw new InvalidOperationException($"A result of value type {valueType} is not supported.");
            }
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException
                                      or KeyNotFoundException or ArgumentException)
        {
            // Names the value type only: the value of a result may be sensitive.
            throw new InvalidOperationException(
                $"The result of the CK method does not fit the declared value type {valueType}.");
        }
    }

    private static List<T> Array<T>(JsonElement json, Func<JsonElement, T> convert)
    {
        if (json.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("An array was expected.");
        }

        return json.EnumerateArray().Select(convert).ToList();
    }

    private static int EnumKey(ICkCacheService ckCacheService, string tenantId, CkId<CkEnumId>? enumId,
        JsonElement json)
    {
        if (enumId == null || !ckCacheService.TryGetRtCkEnum(tenantId, enumId.ToRtCkId(), out var enumGraph) ||
            enumGraph == null)
        {
            throw new InvalidOperationException("The CK enum of the result is unknown.");
        }

        if (json.ValueKind == JsonValueKind.Number)
        {
            var key = json.GetInt32();
            if (enumGraph.Values.Any(v => v.Key == key))
            {
                return key;
            }
        }
        else if (json.ValueKind == JsonValueKind.String)
        {
            // The wire form names the member; tolerate the GraphQL spelling (OK_VALUE) and the CK spelling.
            var wanted = Normalize(json.GetString()!);
            var match = enumGraph.Values.FirstOrDefault(v => Normalize(v.Name) == wanted);
            if (match != null)
            {
                return match.Key;
            }
        }

        throw new InvalidOperationException("The enum value of the result is unknown.");
    }

    private static string Normalize(string name) =>
        name.Replace("_", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static RtRecord ToRecord(ICkCacheService ckCacheService, string tenantId, CkId<CkRecordId>? recordId,
        JsonElement json)
    {
        if (recordId == null || json.ValueKind != JsonValueKind.Object ||
            !ckCacheService.TryGetRtCkRecord(tenantId, recordId.ToRtCkId(), out var recordGraph) || recordGraph == null)
        {
            throw new InvalidOperationException("The CK record of the result is unknown.");
        }

        var record = new RtRecord { CkRecordId = recordGraph.CkRecordId.ToRtCkId() };
        foreach (var attribute in recordGraph.AllAttributes.Values)
        {
            foreach (var property in json.EnumerateObject())
            {
                if (!string.Equals(property.Name, attribute.AttributeName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = Convert(ckCacheService, tenantId, attribute.ValueType, attribute.ValueCkEnumId,
                    attribute.ValueCkRecordId, property.Value, inRecord: true);
                record.SetAttributeRawValue(attribute.AttributeName, value);
                break;
            }
        }

        return record;
    }
}
