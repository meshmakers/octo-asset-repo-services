using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Read-side state of a <c>Secret</c> attribute (AB#5528, concept §4.1). The only thing the API ever
///     tells about a secret is whether it is set: neither the plaintext nor the stored envelope is ever
///     projected. Typed attribute fields of value type <c>Secret</c> use this type; a client that still
///     selects such a field as a scalar fails validation instead of reading a credential.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class OctoSecretStateDtoType : ObjectGraphType<OctoSecretStateDto>
{
    public OctoSecretStateDtoType()
    {
        Name = "OctoSecretState";
        Description = "State of a secret attribute. The value of a secret is never returned by the API; " +
                      "set it with a string in a mutation and clear it with 'clearSecretAttributes'.";

        Field(x => x.IsSet, typeof(NonNullGraphType<BooleanGraphType>))
            .Description("True when the secret holds a value.");
    }
}
