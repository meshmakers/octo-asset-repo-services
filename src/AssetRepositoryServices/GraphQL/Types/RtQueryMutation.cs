using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RtQueryMutation : RtMutationBase
{
    public RtQueryMutation()
    {
        Name = "RtQueryMutations";


        Field<NonNullGraphType<ListGraphType<NonNullGraphType<RtQueryRowDtoType>>>>("create")
            .Description("Create entities of a runtime query.")
            .Argument<NonNullGraphType<ListGraphType<NonNullGraphType<RtQueryRowDtoInputType>>>>(Statics.EntitiesArg)
            .ResolveAsync(ResolveCreate);
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<RtQueryRowDtoType>>>>("update")
            .Description("Updates entities of a runtime query.")
            .Argument<NonNullGraphType<ListGraphType<NonNullGraphType<RtQueryRowDtoUpdateType>>>>(Statics.EntitiesArg)
            .ResolveAsync(ResolveUpdate);
        Field<NonNullGraphType<BooleanGraphType>>("delete")
            .Description("Deletes entities of a runtime query.")
            .Argument<NonNullGraphType<ListGraphType<NonNullGraphType<RtEntityIdType>>>>(Statics.EntitiesArg)
            .ResolveAsync(ResolveDelete);
    }

    private async Task EvaluateNavigationFilters(ICkCacheService ckCacheService, ITenantRepository tenantRepository,
        List<NavigationPair> navigationPairs, List<RtQueryRowDto> inputObjects)
    {
        foreach (var navigationPair in navigationPairs)
        {
            if (navigationPair.InnerNavigationPairs.Any())
            {
                await EvaluateNavigationFilters(ckCacheService, tenantRepository, navigationPair.InnerNavigationPairs,
                    inputObjects);
            }

            var subPathTermsArray = navigationPair.SubPathTerms.Where(pt => pt.First().Type == PathType.Attribute);
            foreach (var subPathTerms in subPathTermsArray)
            {
                var enumerable = subPathTerms.ToArray();
                var pathTerms = navigationPair.PathTerms.Concat(enumerable);
                var attributePath = RtPathEvaluator.GetPath(pathTerms);

                var subAttributePath = RtPathEvaluator.GetPath(enumerable);

                var values = inputObjects.SelectMany(t =>
                        t.Cells?.Where(c => c.AttributePath == attributePath)
                            .Select(c => c.Value) ?? [])
                    .Where(v => v != null).Cast<object>()
                    .Distinct();

                var targetCkTypeGraph =
                    ckCacheService.GetCkType(tenantRepository.TenantId, navigationPair.TargetCkTypeId);
                targetCkTypeGraph.AllAttributesByName.TryGetValue(subAttributePath.ToPascalCase(),
                    out var attributeGraph);
                var attributeValueType = attributeGraph?.ValueType;
                if (attributeValueType == null)
                {
                    switch (subAttributePath.ToPascalCase())
                    {
                        case nameof(RtEntity.RtId):
                            values = values
                                .Select(v =>
                                    (object)OctoObjectId.Parse(v.ToString() ??
                                                               throw AssetRepositoryException
                                                                   .CannotConvertValueToString(v)));
                            break;
                        case nameof(RtEntity.RtWellKnownName):
                            break;
                        case nameof(RtEntity.RtCreationDateTime):
                        case nameof(RtEntity.RtChangedDateTime):
                            attributeValueType = AttributeValueTypesDto.DateTime;
                            break;
                        case nameof(RtEntity.RtVersion):
                            attributeValueType = AttributeValueTypesDto.Int64;
                            break;
                        default:
                            throw AssetRepositoryException.AttributeNotFound(subAttributePath.ToPascalCase(),
                                navigationPair.TargetCkTypeId);
                    }
                }

                if (attributeGraph != null && attributeValueType == AttributeValueTypesDto.String)
                {
                    values = values.Select(v => v.ToString() ?? throw AssetRepositoryException
                        .CannotConvertValueToString(v));
                }

                navigationPair.FieldIn(subAttributePath, values);
            }
        }
    }

    private async Task<object?> ResolveCreate(IResolveFieldContext<object?> context)
    {
        var ckCacheService = context.RequestServices?.GetRequiredService<ICkCacheService>();
        if (ckCacheService == null)
        {
            throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
        }

        var sessionAccessor = context.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor?.Session == null)
        {
            throw AssetRepositoryException.SessionUnavailable();
        }

        if (context.Parent == null)
        {
            throw AssetRepositoryException.ParentUnavailable();
        }

        var tenantContext = Helpers.GetTenantContext(context.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();
        var graphQlUserContext = (GraphQlUserContext)context.UserContext;

        var queryRtId = context.Parent.GetArgument<OctoObjectId>(Statics.RtIdArg);
        var inputObjects = context.GetArgument<List<RtQueryRowDto>>(Statics.EntitiesArg);

        var rtQuery = await tenantRepository.GetRtEntityByRtIdAsync<RtQuery>(sessionAccessor.Session, queryRtId);
        if (rtQuery == null)
        {
            throw AssetRepositoryException.QueryNotFound(queryRtId);
        }

        var navigationPairs = RtPathEvaluator.TokenizeAndGetNavigationPairs(ckCacheService, graphQlUserContext.TenantId,
            rtQuery.QueryCkTypeId,
            rtQuery.Columns);

        await EvaluateNavigationFilters(ckCacheService, tenantRepository, navigationPairs, inputObjects);

        // Find results
        Dictionary<NavigationPair, List<RtEntityGraphItem>> navigationPairToInputObjects = new();
        foreach (var navigationPair in navigationPairs)
        {
            var dataQueryOperation = DataQueryOperation.Create();
            if (navigationPair.FieldFilters == null || !navigationPair.FieldFilters.Any())
            {
                throw AssetRepositoryException.NavigationWithoutRestrictionNotAllowed(navigationPair.CkRoleId,
                    navigationPair.Direction, navigationPair.TargetCkTypeId);
            }

            foreach (var navigationPairFieldFilter in navigationPair.FieldFilters)
            {
                dataQueryOperation.AddFieldFilter(navigationPairFieldFilter.AttributePath,
                    navigationPairFieldFilter.Operator, navigationPairFieldFilter.ComparisonValue);
            }

            var resultSet = await tenantRepository.GetRtEntitiesGraphByTypeAsync(sessionAccessor.Session,
                navigationPair.TargetCkTypeId,
                dataQueryOperation, navigationPair.InnerNavigationPairs);
            navigationPairToInputObjects.Add(navigationPair, resultSet.Items.ToList());
        }


        try
        {
            var entityUpdateInfos = new List<EntityUpdateInfo<RtEntity>>();
            var associationUpdateInfoList = new List<AssociationUpdateInfo>();


            foreach (var queryRowDto in inputObjects)
            {
                var rtEntity = await tenantRepository.CreateTransientRtEntityAsync(queryRowDto.CkTypeId);

                foreach (var navigationPairToInputObject in navigationPairToInputObjects)
                {
                    var subPathTerms =
                        navigationPairToInputObject.Key.SubPathTerms.Where(pt => pt.First().Type == PathType.Attribute)
                            .ToArray();

                    var pathTerms = subPathTerms.Select(spt => navigationPairToInputObject.Key.PathTerms.Concat(spt))
                        .ToArray();
                    var attributePaths = pathTerms.Select(RtPathEvaluator.GetPath);

                    var cellDto = queryRowDto.Cells?.Where(c => attributePaths.Contains(c.AttributePath)).ToArray();
                    if (cellDto != null && cellDto.Any())
                    {
                        var compareValues = cellDto.ToDictionary(k => k.AttributePath, v => v.Value);

                        var candidates = navigationPairToInputObject.Value.Where(t => subPathTerms.All(spt =>
                        {
                            var enumerable = spt as PathTerm[] ?? spt.ToArray();
                            return t.GetAttributeValueByAccessPath(ckCacheService, graphQlUserContext.TenantId,
                                       enumerable)?.ToString() ==
                                   compareValues[
                                           RtPathEvaluator.GetPath(
                                               navigationPairToInputObject.Key.PathTerms.Concat(enumerable))]
                                       ?.ToString();
                        })).ToArray();

                        if (candidates.Any())
                        {
                            var targetRtEntity = candidates.First();

                            if (navigationPairToInputObject.Key.Direction == GraphDirections.Inbound)
                            {
                                associationUpdateInfoList.Add(AssociationUpdateInfo.CreateCreate(
                                    targetRtEntity.ToRtEntityId(), rtEntity.ToRtEntityId(),
                                    navigationPairToInputObject.Key.CkRoleId));
                            }
                            else if (navigationPairToInputObject.Key.Direction == GraphDirections.Outbound)
                            {
                                associationUpdateInfoList.Add(AssociationUpdateInfo.CreateCreate(
                                    rtEntity.ToRtEntityId(), targetRtEntity.ToRtEntityId(),
                                    navigationPairToInputObject.Key.CkRoleId));
                            }
                        }
                    }
                }

                RtEntityFromInputObject(ckCacheService, graphQlUserContext.TenantId, rtEntity, queryRowDto);
                entityUpdateInfos.Add(EntityUpdateInfo<RtEntity>.CreateInsert(rtEntity));
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(sessionAccessor.Session, entityUpdateInfos,
                associationUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                foreach (var message in operationResult.Messages)
                {
                    if (message.MessageLevel == MessageLevel.Error)
                    {
                        context.Errors.Add(new ExecutionError(message.MessageText)
                            { Code = string.Format(Statics.GraphQlOperationError, message.MessageNumber) });
                    }
                    else if (message.MessageLevel == MessageLevel.FatalError)
                    {
                        context.Errors.Add(new ExecutionError(message.MessageText)
                            { Code = string.Format(Statics.GraphQlOperationFatalError, message.MessageNumber) });
                    }
                }

                return null;
            }

            return await GetRtQueryRowResultSet(sessionAccessor.Session, ckCacheService, tenantRepository,
                entityUpdateInfos, queryRtId);
        }
        catch (PersistenceException e)
        {
            context.Errors.Add(new ExecutionError(e.Message, e) { Code = Statics.GraphQlErrorDataStore });
            return null;
        }
        catch (Exception e)
        {
            context.Errors.Add(new ExecutionError("A general error occurred", e)
                { Code = Statics.GraphQlErrorCommon });
            return null;
        }
    }

    private async Task<object?> ResolveDelete(IResolveFieldContext<object?> context)
    {
        var tenantContext = Helpers.GetTenantContext(context.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();

        var sessionAccessor = context.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor?.Session == null)
        {
            throw AssetRepositoryException.SessionUnavailable();
        }

        var inputObjects = context.GetArgument<List<RtEntityIdDto>>(Statics.EntitiesArg);

        try
        {
            var entityUpdateInfos = new List<EntityUpdateInfo<RtEntity>>();
            foreach (var rtEntityId in inputObjects)
            {
                entityUpdateInfos.Add(
                    EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(rtEntityId.CkTypeId, rtEntityId.RtId)));
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
            context.Errors.Add(new ExecutionError(e.Message, e) { Code = Statics.GraphQlErrorDataStore });
            return false;
        }
        catch (Exception e)
        {
            context.Errors.Add(new ExecutionError("A general error occurred", e)
                { Code = Statics.GraphQlErrorCommon });
            return false;
        }
    }

    private async Task<object?> ResolveUpdate(IResolveFieldContext<object?> context)
    {
        var ckCacheService = context.RequestServices?.GetRequiredService<ICkCacheService>();
        if (ckCacheService == null)
        {
            throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
        }

        var sessionAccessor = context.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor?.Session == null)
        {
            throw AssetRepositoryException.SessionUnavailable();
        }

        if (context.Parent == null)
        {
            throw AssetRepositoryException.ParentUnavailable();
        }

        var tenantContext = Helpers.GetTenantContext(context.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();
        var graphQlUserContext = (GraphQlUserContext)context.UserContext;

        var queryRtId = context.Parent.GetArgument<OctoObjectId>(Statics.RtIdArg);
        var inputObjects = context.GetArgument<List<MutationDto<RtQueryRowDto>>>(Statics.EntitiesArg);

        try
        {
            var entityUpdateInfos = new List<EntityUpdateInfo<RtEntity>>();
            foreach (var mutationDto in inputObjects)
            {
                var rtEntityId = new RtEntityId(mutationDto.Item.CkTypeId, mutationDto.RtId);
                var document = new RtEntity
                {
                    RtId = mutationDto.RtId,
                    CkTypeId = mutationDto.Item.CkTypeId
                };

                RtEntityFromInputObject(ckCacheService, graphQlUserContext.TenantId, document,
                    mutationDto.Item);
                if (document.Attributes.Any())
                {
                    entityUpdateInfos.Add(EntityUpdateInfo<RtEntity>.CreateUpdate(rtEntityId, document));
                }
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(sessionAccessor.Session, entityUpdateInfos,
                operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                foreach (var message in operationResult.Messages)
                {
                    if (message.MessageLevel == MessageLevel.Error)
                    {
                        context.Errors.Add(new ExecutionError(message.MessageText)
                            { Code = string.Format(Statics.GraphQlOperationError, message.MessageNumber) });
                    }
                    else if (message.MessageLevel == MessageLevel.FatalError)
                    {
                        context.Errors.Add(new ExecutionError(message.MessageText)
                            { Code = string.Format(Statics.GraphQlOperationFatalError, message.MessageNumber) });
                    }
                }

                return null;
            }

            return await GetRtQueryRowResultSet(sessionAccessor.Session, ckCacheService, tenantRepository,
                entityUpdateInfos, queryRtId);
        }
        catch (OperationFailedException e)
        {
            context.Errors.Add(new ExecutionError(e.Message, e) { Code = Statics.GraphQlErrorDataStore });
            return null;
        }
        catch (Exception e)
        {
            context.Errors.Add(new ExecutionError("A general error occurred", e)
                { Code = Statics.GraphQlErrorCommon });
            return null;
        }
    }
}