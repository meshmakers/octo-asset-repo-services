using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public sealed class UpdateMutationDtoType<TItemType> : InputObjectGraphType<MutationDto<TItemType>>
    where TItemType : class
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