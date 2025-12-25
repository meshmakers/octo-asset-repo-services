using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

/// <summary>
///     Implements a GraphQL input type for runtime entity query row
/// </summary>
internal sealed class RtQueryRowDtoInputType : InputObjectGraphType<RtSimpleQueryRowDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public RtQueryRowDtoInputType()
    {
        Name = $"RtQueryRow{Statics.GraphQlInputSuffix}";

        Field(x => x.CkTypeId, typeof(NonNullGraphType<RtCkIdGraph<CkTypeId>>));
        Field(x => x.RtWellKnownName, true);
        Field(x => x.Cells, typeof(NonNullGraphType<ListGraphType<RtQueryCellDtoInputType>>));
    }
}