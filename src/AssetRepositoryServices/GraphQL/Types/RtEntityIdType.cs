using GraphQL.Types;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     GraphQL type representing the RtEntityId type (struct with CkId and rtId)
/// </summary>
public class RtEntityIdType : InputObjectGraphType<RtEntityId>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public RtEntityIdType()
    {
        Name = "RtEntityId";
        Description = "Id information consists of CkId and RtId";

        Field(x => x.RtId, type: typeof(NonNullGraphType<OctoObjectIdType>)).Description("Unique id of the object.");
        Field(x => x.CkId, type: typeof(NonNullGraphType<StringGraphType>))
            .Description("Construction kit id of the object.");
    }
}
