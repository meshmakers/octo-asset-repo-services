using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using GraphQL;
using GraphQL.Resolvers;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     Implements GraphQL subscriptions
/// </summary>
public class OctoSubscriptions : ObjectGraphType<object>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="rtEntityDtoTypes">RT Entity types subscriptions are created.</param>
    public OctoSubscriptions(IEnumerable<RtEntityDtoType> rtEntityDtoTypes)
    {
        foreach (var rtEntityDtoType in rtEntityDtoTypes)
            // ReSharper disable once VirtualMemberCallInConstructor
        {
            AddField(new FieldType
            {
                Name = $"{rtEntityDtoType.Name}Events",
                Arguments = new QueryArguments(
                    new QueryArgument<OctoObjectIdType> { Name = Statics.RtIdArg },
                    new QueryArgument<NonNullGraphType<ListGraphType<UpdateTypesDtoType>>>
                        { Name = Statics.UpdateTypesArg }
                ),
                ResolvedType =
                    new DynamicUpdateMessageDtoType<RtEntityUpdateItemDto>(
                        new RtEntityUpdateItemDtoType(rtEntityDtoType)),
                Resolver = new FuncFieldResolver<DynamicUpdateMessageDto<RtEntityUpdateItemDto>>(ResolveMessage),
                StreamResolver = new SourceStreamResolver<DynamicUpdateMessageDto<RtEntityUpdateItemDto>>(Subscribe)
            }).AddMetadata(Statics.CkId, rtEntityDtoType.CkId);
        }
    }

    private IObservable<DynamicUpdateMessageDto<RtEntityUpdateItemDto>> Subscribe(IResolveFieldContext context)
    {
        var tenantContext = Helpers.GetTenantContext(context.UserContext);

        var ckId = context.FieldDefinition.GetMetadata<string>(Statics.CkId);
        var rtId = context.GetArgument<OctoObjectId?>(Statics.RtIdArg);

        var updateTypeDtoList = context.GetArgument<ICollection<UpdateTypesDto>>(Statics.UpdateTypesArg);
        var updateType = UpdateTypes.Undefined;
        foreach (var updateTypeDto in updateTypeDtoList)
        {
            updateType |= (UpdateTypes)updateTypeDto;
        }

        var updateStreamFilter = new UpdateStreamFilter
        {
            UpdateTypes = updateType,
            RtId = rtId
        };

        var messages =
            tenantContext.Repository.SubscribeToRtEntities(ckId, updateStreamFilter, context.CancellationToken);

        var observable = messages.GetUpdates().Select(x => new DynamicUpdateMessageDto<RtEntityUpdateItemDto>
        {
            Items = new List<RtEntityUpdateItemDto>
            {
                new() { UserContext = x.Document, UpdateState = (UpdateTypesDto)x.UpdateType }
            }
        });

        return observable;
    }

    private DynamicUpdateMessageDto<RtEntityUpdateItemDto> ResolveMessage(IResolveFieldContext context)
    {
        var message = context.Source as DynamicUpdateMessageDto<RtEntityUpdateItemDto>;

        return message;
    }
}
