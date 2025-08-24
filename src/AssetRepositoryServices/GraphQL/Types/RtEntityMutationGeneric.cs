using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class RtEntityMutationGeneric : RtMutationBase
{
    public RtEntityMutationGeneric()
    {
        Name = "RtEntityMutations";


        var deleteArgument =
            new QueryArgument(typeof(NonNullGraphType<ListGraphType<RtEntityIdType>>))
                { Name = Statics.EntitiesArg };

        this.FieldAsync($"delete",
            $"Deletes an runtime entity.",
            new BooleanGraphType(),
            new QueryArguments(deleteArgument), ResolveDelete);
    }

    private async ValueTask<object?> ResolveDelete(IResolveFieldContext<object?> arg)
    {
        var tenantContext = Helpers.GetTenantContext(arg.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();
        
        var sessionAccessor = arg.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor?.Session == null)
        {
            throw AssetRepositoryException.SessionUnavailable();
        }

        var inputObjects = arg.GetArgument<List<RtEntityIdDto>>(Statics.EntitiesArg);

        try
        {
            var entityUpdateInfos = new List<EntityUpdateInfo<RtEntity>>();
            foreach (var rtEntityIdDto in inputObjects)
            {
                entityUpdateInfos.Add(EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(rtEntityIdDto.CkTypeId, rtEntityIdDto.RtId)));
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(sessionAccessor.Session, entityUpdateInfos, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                return false;
            }

            return true;
        }
        catch (OperationFailedException e)
        {
            arg.Errors.Add(new ExecutionError(e.Message, e) { Code = Statics.GraphQlErrorDataStore });
            return false;
        }
        catch (Exception e)
        {
            arg.Errors.Add(new ExecutionError("A general error occurred", e)
                { Code = Statics.GraphQlErrorCommon });
            return false;
        }
    }
}