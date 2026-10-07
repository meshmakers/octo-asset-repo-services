using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Read-side state of a <c>Secret</c> attribute (AB#5528, concept §4.1, handover §2). The API only tells
///     whether a secret is set, whether a stored value cannot be read because its key id is missing, and when it
///     was set: neither the plaintext nor the stored envelope is ever projected. Typed attribute fields of value type <c>Secret</c> use this type; a client that still
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
            .Description("True when the secret holds a readable value. False when it is not set or when the " +
                         "stored value cannot be read (key missing, corrupt).");
        Field(x => x.KeyMissing, typeof(NonNullGraphType<BooleanGraphType>))
            .Description("True when a value is stored but its key id is not in this environment's key ring " +
                         "(e.g. after a restore from another environment); the secret has to be entered again.");
        Field(x => x.SetAt, typeof(UtcDateTimeGraphType))
            .Description("When the current value was set (UTC). Null when not set or for values set before " +
                         "this was recorded (legacy).");
    }
}
