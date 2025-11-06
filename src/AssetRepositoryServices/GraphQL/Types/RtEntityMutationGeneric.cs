using AssetRepositoryServices.Resources;
using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class RtEntityMutationGeneric : RtMutationBase
{
    public RtEntityMutationGeneric()
    {
        Name = "RtEntityMutations";

        var deleteArgument =
            new QueryArgument(typeof(NonNullGraphType<ListGraphType<RtEntityIdType>>))
                { Name = Statics.EntitiesArg, Description = AssetTexts.Graphql_Arguments_Entities_Description };
        var strategyArgument =
            new QueryArgument(typeof(DeleteOptionsDtoType))
            {
                Name = Statics.OptionsArg, DefaultValue = DeleteStrategies.Archive,
                Description = AssetTexts.Graphql_Arguments_Options_Description
            };
        this.FieldAsync("delete",
            AssetTexts.Graphql_RtEntityMutationGeneric_DeleteOperation_Description,
            new BooleanGraphType(),
            new QueryArguments(deleteArgument, strategyArgument), ResolveDelete);
    }

    private async ValueTask<object?> ResolveDelete(IResolveFieldContext<object?> arg)
    {
        var tenantContext = Helpers.GetTenantContext(arg.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();
        var sessionAccessor = arg.GetSessionAccessor();

        var inputObjects = arg.GetArgument<List<RtEntityIdDto>>(Statics.EntitiesArg);
        arg.TryGetArgument(Statics.OptionsArg, DeleteStrategies.Archive, out var deleteStrategy);

        try
        {
            var entityUpdateInfos = new List<EntityUpdateInfo<RtEntity>>();
            foreach (var rtEntityIdDto in inputObjects)
            {
                entityUpdateInfos.Add(
                    EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(rtEntityIdDto.CkTypeId,
                        rtEntityIdDto.RtId)));
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(sessionAccessor.Session, entityUpdateInfos,
                new DeleteOptions { Strategy = deleteStrategy },
                operationResult);
            ResolveConnectionContextExtensions.ValidateOperationResult(operationResult);

            return true;
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }
}