using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements an update item for RtEntities
/// </summary>
public class RtEntityUpdateItemDtoType : ObjectGraphType<RtEntityUpdateItemDto>
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
        var rtEntity = (RtEntity)arg.Source.UserContext;

        return RtEntityDtoType.CreateRtEntityDto(rtEntity);
    }
}
