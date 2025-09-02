using GraphQL.Types;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class RtEntityDtoGenericUpdateType : InputObjectGraphType<MutationDto<RtEntityDto>>
{
    public RtEntityDtoGenericUpdateType()
    {
        Name = $"RtEntity{Statics.GraphQlUpdatePrefix}".ToPascalCase();
        Field(x => x.RtId, typeof(OctoObjectIdType));
        Field<RtEntityDtoGenericInputType>("item").Description("Item to update");
    }
}