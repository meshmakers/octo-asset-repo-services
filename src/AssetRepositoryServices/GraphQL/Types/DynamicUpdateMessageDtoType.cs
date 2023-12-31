using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements the GraphQL type used for subscription messages
/// </summary>
public class DynamicUpdateMessageDtoType<TItemType> : ObjectGraphType<DynamicUpdateMessageDto<TItemType>>
    where TItemType : GraphQlDto
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="itemType">The GraphQL type used as item type.</param>
    public DynamicUpdateMessageDtoType(IGraphType itemType)
    {
        Name = $"{itemType.Name}{CommonConstants.GraphQlUpdateMessageSuffix}";
        this.Field("Items", "The corresponding items", new ListGraphType(itemType));
    }
}
