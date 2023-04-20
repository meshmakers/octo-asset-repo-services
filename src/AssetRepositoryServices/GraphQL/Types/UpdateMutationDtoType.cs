using GraphQL.Types;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public class UpdateMutationDtoType<TItemType> : InputObjectGraphType<MutationDto<TItemType>>
{
    public UpdateMutationDtoType(IGraphType itemType)
    {
        Name = $"{CommonConstants.GraphQlUpdatePrefix}{itemType.Name}";
        Field(x => x.RtId, type: typeof(OctoObjectIdType));
        this.Field("item",
            "Item to update",
            new NonNullGraphType(itemType));
    }
}
