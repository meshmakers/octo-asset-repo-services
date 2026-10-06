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
        // AB#5528 (concept §4.3)
        Field(x => x.ClearSecretAttributes, typeof(ListGraphType<NonNullGraphType<StringGraphType>>))
            .Description("Names of Secret attributes to clear (camelCase, like the item's attribute fields). Clearing is " +
                         "always explicit: a secret that is omitted, null or \"\" in the item stays unchanged. " +
                         "Clearing a required secret is an error.");
    }
}