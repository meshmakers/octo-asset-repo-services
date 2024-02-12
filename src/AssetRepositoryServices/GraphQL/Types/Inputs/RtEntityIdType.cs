using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

/// <summary>
///     GraphQL type representing the RtEntityId type (struct with CkId and rtId)
/// </summary>
internal sealed class RtEntityIdType : InputObjectGraphType<RtEntityIdDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public RtEntityIdType()
    {
        Name = "RtEntityId";
        Description = "Id information consists of CkId and RtId";

        Field(x => x.RtId, type: typeof(NonNullGraphType<OctoObjectIdType>)).Description("Unique id of the object.");
        Field(x => x.CkTypeId, type: typeof(NonNullGraphType<StringGraphType>))
            .Description("Construction kit id of the object.");
    }
}