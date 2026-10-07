using System.Collections;
using GraphQL;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

/// <summary>
///     The single read-side rule for <c>Secret</c> attributes in the GraphQL layer (AB#5528, concept §4.1/§4.2):
///     a secret is projected as "is set", "key missing" and "set at" only (handover §2). Every output path (typed
///     fields, generic attributes, records, query cells, mutation responses, subscriptions) goes through these
///     helpers. Nothing in here can return the plaintext, the legacy clear text or the stored envelope.
/// </summary>
internal static class SecretAttributeProjection
{
    /// <summary>
    ///     Read state of a value found in a Secret attribute slot (handover §2, decision 2026-10-06 item 2): a
    ///     protected value whose key id is not in the key ring, and a corrupt stored value, read as not set; the
    ///     former additionally as <c>keyMissing</c>. Decides by the slot (the caller knows the CK attribute is
    ///     Secret), not by the runtime type: a change-stream document carries legacy plain strings that are not
    ///     normalised to <see cref="RtSecretValue" />. Never decrypts.
    /// </summary>
    /// <param name="rawValue">The raw attribute value</param>
    /// <param name="protector">The key ring of this process; <c>null</c> = unknown (then <c>keyMissing</c> is false)</param>
    internal static SecretReadInfo Describe(object? rawValue, ISecretAttributeProtector? protector)
    {
        var value = ToSecretValue(rawValue);
        return protector != null
            ? protector.DescribeSecret(value)
            : SecretValueStates.Describe(value, null);
    }

    /// <summary>
    ///     The typed <c>OctoSecretState</c> of a value found in a Secret attribute slot (see <see cref="Describe" />).
    /// </summary>
    internal static OctoSecretStateDto ToSecretState(object? rawValue, ISecretAttributeProtector? protector)
    {
        var info = Describe(rawValue, protector);
        return new OctoSecretStateDto(info.IsSet)
        {
            KeyMissing = info.KeyMissing,
            SetAt = info.IsSet || info.KeyMissing ? info.SetAt : null
        };
    }

    /// <summary>
    ///     The generic projection of a Secret attribute: <c>value = null</c> plus <c>secretIsSet</c>,
    ///     <c>secretKeyMissing</c> and <c>secretSetAt</c> (see <see cref="Describe" />).
    /// </summary>
    internal static RtEntityAttributeDto ToAttributeDto(string attributeName, object? rawValue,
        ISecretAttributeProtector? protector)
    {
        var state = ToSecretState(rawValue, protector);
        return new RtEntityAttributeDto
        {
            AttributeName = attributeName,
            Value = null,
            SecretIsSet = state.IsSet,
            SecretKeyMissing = state.KeyMissing,
            SecretSetAt = state.SetAt
        };
    }

    /// <summary>
    ///     The key ring of the request, if registered (<c>AddRuntimeEngine()</c> always registers one).
    /// </summary>
    internal static ISecretAttributeProtector? GetProtector(this IResolveFieldContext context)
    {
        return context.RequestServices?.GetService<ISecretAttributeProtector>();
    }

    /// <summary>
    ///     Last line of defence for untyped values (generic scalars, query cells): an
    ///     <see cref="RtSecretValue" /> becomes <c>null</c>, also inside lists. Records keep their shape; their
    ///     secret members are written as the marker <c>{"isSet":...}</c> by the serializer converters.
    /// </summary>
    internal static object? RedactUntyped(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case RtSecretValue:
                return null;
            case string:
                return value;
            case IList list when ContainsSecret(list):
                return list.Cast<object?>().Select(item => item is RtSecretValue ? null : item).ToList();
            default:
                return value;
        }
    }

    private static RtSecretValue? ToSecretValue(object? rawValue)
    {
        return rawValue switch
        {
            null => null,
            RtSecretValue secretValue => secretValue,
            string legacy => RtSecretValue.LegacyPlaintext(legacy),
            // Anything else in a Secret slot is foreign data: report it as "set" without looking at it.
            _ => RtSecretValue.LegacyPlaintext("*")
        };
    }

    private static bool ContainsSecret(IList list)
    {
        foreach (var item in list)
        {
            if (item is RtSecretValue)
            {
                return true;
            }
        }

        return false;
    }
}
