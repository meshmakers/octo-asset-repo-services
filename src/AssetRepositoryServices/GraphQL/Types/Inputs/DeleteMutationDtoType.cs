using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

public sealed class DeleteMutationDtoType : InputObjectGraphType<MutationDto<object>>
{
    public DeleteMutationDtoType(IGraphType itemType)
    {
        Name = $"{CommonConstants.GraphQlDeletePrefix}{itemType.Name}";
        Field(x => x.RtId, type: typeof(OctoObjectIdType));
    }
}