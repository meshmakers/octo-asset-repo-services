using System.Collections;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

/// <summary>
///     The single read-side rule for <c>Secret</c> attributes in the GraphQL layer (AB#5528, concept §4.1/§4.2):
///     a secret is projected as "is set" only. Every output path (typed fields, generic attributes, records,
///     query cells, mutation responses, subscriptions) goes through these helpers. Nothing in here can return
///     the plaintext, the legacy clear text or the stored envelope.
/// </summary>
internal static class SecretAttributeProjection
{
    /// <summary>
    ///     State of a value found in a Secret attribute slot. Decides by the slot (the caller knows the CK
    ///     attribute is Secret), not by the runtime type: a change-stream document carries legacy plain
    ///     strings that are not normalised to <see cref="RtSecretValue" />.
    /// </summary>
    internal static OctoSecretStateDto ToSecretState(object? rawValue)
    {
        return OctoSecretStateDto.FromValue(rawValue);
    }

    /// <summary>
    ///     Whether a value found in a Secret attribute slot counts as set (see <see cref="OctoSecretStateDto.IsValueSet" />).
    /// </summary>
    internal static bool IsSet(object? rawValue)
    {
        return OctoSecretStateDto.IsValueSet(rawValue);
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
