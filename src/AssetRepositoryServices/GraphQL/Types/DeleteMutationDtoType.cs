using GraphQL.Types;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public class DeleteMutationDtoType : InputObjectGraphType<MutationDto<object>>
{
    public DeleteMutationDtoType(IGraphType itemType)
    {
        Name = $"{CommonConstants.GraphQlDeletePrefix}{itemType.Name}";
        Field(x => x.RtId, type: typeof(OctoObjectIdType));
    }
}
