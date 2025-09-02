using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

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
        Description = "Id information consists of CkTypeId and RtId";

        Field(x => x.RtId, typeof(NonNullGraphType<OctoObjectIdType>)).Description("Unique id of the object.");
        Field(x => x.CkTypeId, typeof(NonNullGraphType<CkIdGraph<CkTypeId>>))
            .Description("Construction kit type id of the object.");
    }
}