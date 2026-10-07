using System.Collections;
using System.Collections.Concurrent;
using GraphQL;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

/// <summary>
///     Generic attribute projection for a runtime object whose CK type, record or association role is not in the
///     tenant's CK cache (AB#5535) - typically an entity of an outdated CK model version that a model update removed
///     (live: <c>System.Communication/AiConfiguration</c> in tenant <c>meshmakers</c>). Such an entity used to fail
///     the whole list query with a CK cache error; it is now projected from its stored attributes instead.
/// </summary>
/// <remarks>
///     <para>
///         <b>Rule (the CK knowledge is missing, so the value decides):</b>
///     </para>
///     <list type="bullet">
///         <item>
///             A value that is recognisably a secret - an <see cref="RtSecretValue" /> (the BSON serializer turns the
///             stored <c>{ _t: "OctoSecret" }</c> sub-document into one in every slot), an <c>enc:v1</c> /
///             <c>enc:v2</c> envelope string, or a not-yet-deserialised <c>OctoSecret</c> sub-document - is projected
///             like a Secret attribute: <c>value = null</c> plus <c>secretIsSet</c> / <c>secretKeyMissing</c> /
///             <c>secretSetAt</c>. Inside lists such items become <c>null</c>.
///         </item>
///         <item>
///             Records are projected recursively (their CK record is looked up and, if unknown as well, projected by
///             this rule).
///         </item>
///         <item>
///             Every other value is returned as stored. A legacy clear-text string in what used to be a Secret slot of
///             the unknown type cannot be told apart from an ordinary string; it is returned as stored, which is the
///             behaviour for such data before the Secret value type existed. This is accepted because the type is no
///             longer part of the tenant's model, and logged once per tenant and type as a warning (never with values),
///             so the outdated data can be found and cleaned up (migrate or delete the entities).
///         </item>
///         <item>Enum values are not resolved to names (the enum is not known either).</item>
///     </list>
///     <para>
///         This only applies when the tenant's CK cache is loaded and the type is missing from it. An unloaded cache
///         still fails loudly - degrading there would treat every type as unknown.
///     </para>
/// </remarks>
internal static class UnknownCkTypeAttributeProjection
{
    private const int MaxWarnedKeys = 1024;
    private static readonly ConcurrentDictionary<string, byte> WarnedKeys = new();

    /// <summary>
    ///     Projects the stored attributes of <paramref name="rtObject" /> without CK knowledge (see the class remarks).
    /// </summary>
    /// <param name="rtObject">The runtime entity, record or association</param>
    /// <param name="filterAttributeNames">camelCase attribute names to return; <c>null</c> = all</param>
    /// <param name="createRecordDto">Projection of a record value (resolves the record's CK record itself)</param>
    /// <param name="protector">The key ring of this process; <c>null</c> = unknown</param>
    /// <param name="isHiddenName">CK v2: stored names to drop because they are Hidden somewhere in the tenant</param>
    internal static List<RtEntityAttributeDto> Project(RtTypeWithAttributes rtObject,
        IReadOnlyCollection<string>? filterAttributeNames, Func<RtRecord, object?> createRecordDto,
        ISecretAttributeProtector? protector, Func<string, bool>? isHiddenName = null)
    {
        var result = new List<RtEntityAttributeDto>();
        foreach (var (storedName, value) in rtObject.Attributes)
        {
            var attributeName = storedName.ToCamelCase();
            if (filterAttributeNames != null && !filterAttributeNames.Contains(attributeName))
            {
                continue;
            }

            // CK v2 (review L11): without a CK type the access of a stored attribute is unknown - fail closed for
            // every name that is Hidden anywhere in the tenant.
            if (isHiddenName?.Invoke(storedName) == true)
            {
                continue;
            }

            if (IsRecognisableSecret(value))
            {
                result.Add(SecretAttributeProjection.ToAttributeDto(attributeName, value, protector));
                continue;
            }

            result.Add(new RtEntityAttributeDto
            {
                AttributeName = attributeName,
                Value = ProjectValue(value, createRecordDto)
            });
        }

        return result;
    }

    /// <summary>
    ///     True for a value that is a secret without knowing the CK attribute: an <see cref="RtSecretValue" />, an
    ///     envelope string or an <c>OctoSecret</c> sub-document.
    /// </summary>
    internal static bool IsRecognisableSecret(object? value)
    {
        switch (value)
        {
            case RtSecretValue:
                return true;
            case string text:
                return SecretEnvelope.IsEnvelope(text);
            case IDictionary<string, object?> document:
                return document.TryGetValue("_t", out var discriminator) && IsSecretDiscriminator(discriminator);
            case IDictionary document:
                return document.Contains("_t") && IsSecretDiscriminator(document["_t"]);
            default:
                return false;
        }
    }

    /// <summary>
    ///     Logs once per tenant, kind and id that a runtime object of an unknown CK element was projected generically.
    ///     Never logs attribute values.
    /// </summary>
    internal static void WarnOnce(ILogger? logger, string tenantId, string kind, string ckId)
    {
        if (logger == null)
        {
            return;
        }

        var key = $"{tenantId}|{kind}|{ckId}";
        if (WarnedKeys.ContainsKey(key) || WarnedKeys.Count >= MaxWarnedKeys || !WarnedKeys.TryAdd(key, 0))
        {
            return;
        }

        logger.LogWarning(
            "Tenant '{TenantId}': {Kind} '{CkId}' is not in the CK cache (outdated or removed model element). " +
            "Attributes are projected without CK knowledge: recognisable secrets are masked, other values are " +
            "returned as stored. Migrate or delete the affected runtime data",
            tenantId, kind, ckId);
    }

    private static object? ProjectValue(object? value, Func<RtRecord, object?> createRecordDto)
    {
        switch (value)
        {
            case RtRecord rtRecord:
                return createRecordDto(rtRecord);
            case string:
                return value;
            case IEnumerable<object> items:
                return items.Select(item => item switch
                {
                    RtRecord itemRecord => createRecordDto(itemRecord),
                    _ when IsRecognisableSecret(item) => null,
                    _ => item
                }).ToList();
            default:
                return value;
        }
    }

    private static bool IsSecretDiscriminator(object? discriminator)
    {
        return string.Equals(discriminator?.ToString(), "OctoSecret", StringComparison.Ordinal);
    }
}
