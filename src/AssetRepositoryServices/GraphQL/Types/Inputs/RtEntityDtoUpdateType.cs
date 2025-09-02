using GraphQL.Types;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class UpdateMutationDtoType<TItemType> : InputObjectGraphType<MutationDto<TItemType>>
    where TItemType : class
{
    public UpdateMutationDtoType(IGraphType itemType)
    {
        Name = $"{itemType.Name}{Statics.GraphQlUpdatePrefix}".ToPascalCase();
        Field(x => x.RtId, typeof(OctoObjectIdType));
        this.Field("item",
            "Item to update",
            new NonNullGraphType(itemType));
    }
}