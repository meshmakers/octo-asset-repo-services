using GraphQL.Types;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class UpdateMutationDtoGeneric: InputObjectGraphType<MutationDto<RtEntityDto>>
{
    public UpdateMutationDtoGeneric()
    {
        Name = $"RtEntity{Statics.GraphQlUpdatePrefix}".ToPascalCase();
        Field(x => x.RtId, type: typeof(OctoObjectIdType));
        Field<RtEntityDtoGenericInputType>("item").Description("Item to update");
    }
}