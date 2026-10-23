using System.Text.Json;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;

/// <summary>
///     AB#7026: the parameter check of the gateway, run after authorization and the target lookup and before dispatch.
///     It judges the parameters JSON against the declared parameters of the method: a required parameter is missing, a
///     member is not declared, or a value has a JSON kind that does not fit the declared value type. The answer names
///     the offending <b>members</b> and a fixed reason per kind of problem; it never contains a value, so a sensitive
///     parameter cannot leak through the error.
/// </summary>
internal static class CkMethodParameterValidator
{
    /// <summary>Reason: a required parameter is missing (or null).</summary>
    public const string MissingRequired = "missing-required";

    /// <summary>Reason: the member is not a declared parameter.</summary>
    public const string UnknownMember = "unknown-member";

    /// <summary>Reason: the JSON kind of the value does not fit the declared value type.</summary>
    public const string WrongType = "wrong-type";

    /// <summary>
    ///     Returns <c>null</c> when the parameters are valid, otherwise the <c>METHOD_VALIDATION_FAILED</c> error with
    ///     <c>methodId</c>, <c>field</c> (member names, sorted, comma separated) and <c>reason</c> (the distinct reasons,
    ///     sorted, comma separated).
    /// </summary>
    public static CkMethodError? Validate(string qualifiedMethodId, CkMethodDto definition, string? parametersJson)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var declared = definition.Parameters ?? [];
        var problems = new SortedDictionary<string, string>(StringComparer.Ordinal);

        JsonDocument? document = null;
        try
        {
            if (string.IsNullOrWhiteSpace(parametersJson))
            {
                document = null;
            }
            else
            {
                try
                {
                    document = JsonDocument.Parse(parametersJson);
                }
                catch (JsonException)
                {
                    return Failed(qualifiedMethodId, new SortedDictionary<string, string>(StringComparer.Ordinal)
                    {
                        [string.Empty] = WrongType
                    });
                }

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return Failed(qualifiedMethodId, new SortedDictionary<string, string>(StringComparer.Ordinal)
                    {
                        [string.Empty] = WrongType
                    });
                }
            }

            var present = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (document != null)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (declared.All(p => !string.Equals(p.Name, property.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        problems[property.Name] = UnknownMember;
                        continue;
                    }

                    present[property.Name] = property.Value.Clone();
                }
            }

            foreach (var parameter in declared)
            {
                var hasValue = present.TryGetValue(parameter.Name, out var value) &&
                               value.ValueKind != JsonValueKind.Null;
                if (!hasValue)
                {
                    if (!parameter.IsOptional)
                    {
                        problems[parameter.Name] = MissingRequired;
                    }

                    continue;
                }

                if (!Fits(parameter.ValueType, value.ValueKind))
                {
                    problems[parameter.Name] = WrongType;
                }
            }
        }
        finally
        {
            document?.Dispose();
        }

        return problems.Count == 0 ? null : Failed(qualifiedMethodId, problems);
    }

    private static CkMethodError Failed(string qualifiedMethodId, SortedDictionary<string, string> problems) =>
        CkMethodError.Create(CkMethodErrorCodes.MethodValidationFailed,
            (CkMethodErrorParameters.MethodId, qualifiedMethodId),
            (CkMethodErrorParameters.Field, string.Join(",", problems.Keys.Where(k => k.Length > 0))),
            (CkMethodErrorParameters.Reason, string.Join(",", problems.Values.Distinct().Order(StringComparer.Ordinal))));

    private static bool Fits(AttributeValueTypesDto valueType, JsonValueKind kind) => valueType switch
    {
        AttributeValueTypesDto.String or AttributeValueTypesDto.DateTime or AttributeValueTypesDto.DateTimeOffset or
            AttributeValueTypesDto.Binary or AttributeValueTypesDto.Secret => kind == JsonValueKind.String,
        AttributeValueTypesDto.Int or AttributeValueTypesDto.Int64 or AttributeValueTypesDto.Double =>
            kind == JsonValueKind.Number,
        AttributeValueTypesDto.Boolean => kind is JsonValueKind.True or JsonValueKind.False,
        AttributeValueTypesDto.TimeSpan or AttributeValueTypesDto.Enum => kind is JsonValueKind.String
            or JsonValueKind.Number,
        AttributeValueTypesDto.StringArray or AttributeValueTypesDto.IntArray or AttributeValueTypesDto.RecordArray =>
            kind == JsonValueKind.Array,
        AttributeValueTypesDto.Record => kind == JsonValueKind.Object,
        _ => true
    };
}
