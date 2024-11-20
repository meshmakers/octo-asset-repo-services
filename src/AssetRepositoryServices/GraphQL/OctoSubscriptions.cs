using System.Reactive.Linq;
using GraphQL;
using GraphQL.Resolvers;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     Implements GraphQL subscriptions
/// </summary>
[DoNotRegister]
internal class OctoSubscriptions : ObjectGraphType<object>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="graphTypesCache">The graph type cache</param>
    public OctoSubscriptions(IGraphTypesCache graphTypesCache)
    {
        foreach (var rtEntityDtoType in graphTypesCache.GetTypes())
            // ReSharper disable once VirtualMemberCallInConstructor
        {
            AddField(new FieldType
            {
                Name = $"{rtEntityDtoType.Name}Events",
                Arguments = new QueryArguments(
                    new QueryArgument<OctoObjectIdType> { Name = Statics.RtIdArg },
                    new QueryArgument<NonNullGraphType<ListGraphType<UpdateTypesDtoType>>>
                        { Name = Statics.UpdateTypesArg },
                    new QueryArgument<ListGraphType<FieldFilterDtoType>>{ Name = Statics.FieldBeforeFilterArg},
                    new QueryArgument<ListGraphType<FieldFilterDtoType>>{ Name = Statics.FieldFilterArg}
                ),
                ResolvedType =
                    new DynamicUpdateMessageDtoType<RtEntityUpdateItemDto>(
                        new RtEntityUpdateItemDtoType(rtEntityDtoType)),
                Resolver = new FuncFieldResolver<DynamicUpdateMessageDto<RtEntityUpdateItemDto>>(ResolveMessage),
                StreamResolver = new SourceStreamResolver<DynamicUpdateMessageDto<RtEntityUpdateItemDto>>(Subscribe)
            }).AddMetadata(Statics.CkId, rtEntityDtoType.CkTypeId);
        }
    }

    private IObservable<DynamicUpdateMessageDto<RtEntityUpdateItemDto>> Subscribe(IResolveFieldContext context)
    {
        var tenantContext = Helpers.GetTenantContext(context.UserContext);

        var ckId = context.FieldDefinition.GetMetadata<string>(Statics.CkId);
        var rtId = context.GetArgument<OctoObjectId?>(Statics.RtIdArg);

        ICollection<FieldFilter>? beforeFieldFilters = null;
        ICollection<FieldFilter>? fieldFilters = null;
        if (context.TryGetArgument(Statics.FieldBeforeFilterArg, out IEnumerable<FieldFilterDto>? beforeFieldFilterDtoList))
        {
            beforeFieldFilters = new List<FieldFilter>();
            foreach (var beforeFieldFilterDto in beforeFieldFilterDtoList)
            {
                beforeFieldFilters.Add(
                    new(beforeFieldFilterDto.AttributeName.ToPascalCase(),
                        (FieldFilterOperator)beforeFieldFilterDto.Operator, beforeFieldFilterDto.ComparisonValue));
            }
        }
        if (context.TryGetArgument(Statics.FieldFilterArg, out IEnumerable<FieldFilterDto>? fieldFilterDtoList))
        {
            fieldFilters = new List<FieldFilter>();
            foreach (var fieldFilterDto in fieldFilterDtoList)
            {
                fieldFilters.Add(
                    new(fieldFilterDto.AttributeName.ToPascalCase(),
                        (FieldFilterOperator)fieldFilterDto.Operator, fieldFilterDto.ComparisonValue));
            }
        }

        var updateTypeDtoList = context.GetArgument<ICollection<UpdateTypesDto>>(Statics.UpdateTypesArg);
        var updateType = UpdateTypes.Undefined;
        foreach (var updateTypeDto in updateTypeDtoList)
        {
            updateType |= (UpdateTypes)updateTypeDto;
        }

        var watchStreamFilter = new WatchStreamFilter
        {
            UpdateTypes = updateType,
            RtId = rtId,
            BeforeFieldFilters = beforeFieldFilters,
            FieldFilters = fieldFilters,
        };

        var tenantRepository = tenantContext.GetTenantRepository();
        var messages = tenantRepository.WatchRtEntitiesAsync(ckId, watchStreamFilter, context.CancellationToken);

        var observable = messages.Result.GetUpdates().Select(x => new DynamicUpdateMessageDto<RtEntityUpdateItemDto>
        {
            Items = new List<RtEntityUpdateItemDto>
            {
                new() { UserContext = x.Document, UpdateState = (UpdateTypesDto)x.UpdateType }
            }
        });

        return observable;
    }

    private DynamicUpdateMessageDto<RtEntityUpdateItemDto>? ResolveMessage(IResolveFieldContext context)
    {
        var message = context.Source as DynamicUpdateMessageDto<RtEntityUpdateItemDto>;

        return message;
    }
}