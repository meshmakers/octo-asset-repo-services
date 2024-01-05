using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements an update item for RtEntities
/// </summary>
public sealed class RtEntityUpdateItemDtoType : ObjectGraphType<RtEntityUpdateItemDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="rtEntityDtoType">GraphQL type the corresponding RtEntity type</param>
    public RtEntityUpdateItemDtoType(RtEntityDtoType rtEntityDtoType)
    {
        Name = $"{rtEntityDtoType.Name}{CommonConstants.GraphQlUpdateSuffix}";
        this.Field("Item", "The corresponding item", rtEntityDtoType, resolve: ResolveItem);
        Field(o => o.UpdateState, type: typeof(UpdateTypesDtoType));
    }

    private object ResolveItem(IResolveFieldContext<RtEntityUpdateItemDto> arg)
    {
        // TODO: Check if this is correct
        var rtEntity = (RtEntity)arg.Source.UserContext!;

        return RtEntityDtoType.CreateRtEntityDto(rtEntity);
    }
}