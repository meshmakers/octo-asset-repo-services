using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

/// <summary>
///     Implements a GraphQL runtime entity type
/// </summary>
internal sealed class RtEntityDtoGenericInputType : InputObjectGraphType<RtEntityDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public RtEntityDtoGenericInputType()
    {
        Name = $"RtEntity{Statics.GraphQlInputSuffix}";

        Field(x => x.CkTypeId, typeof(NonNullGraphType<RtCkIdGraph<CkTypeId>>));
        Field(x => x.RtWellKnownName, true);
        Field(x => x.Attributes, typeof(NonNullGraphType<ListGraphType<RtEntityAttributeDtoInputType>>));
    }
}