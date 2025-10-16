using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Mapping;

internal class QueryMapperEngine
{


    internal async Task<QueryMapper> CreateQueryMapperAsync(ICkCacheService ckCacheService, GraphQlUserContext graphQlUserContext,
        RtQuery rtQuery, ITenantRepository tenantRepository, List<RtQueryRowDto> inputObjects,
        IOctoSessionAccessor sessionAccessor)
    {
        var navigationPairToInputObjects = await NavigationPairToInputObjects(ckCacheService, graphQlUserContext,
            rtQuery, tenantRepository, inputObjects, sessionAccessor);
        return new QueryMapper(sessionAccessor, ckCacheService, tenantRepository, graphQlUserContext.TenantId, navigationPairToInputObjects);
    }

    private async Task<Dictionary<NavigationPair, List<RtEntityGraphItem>>> NavigationPairToInputObjects(
        ICkCacheService ckCacheService, GraphQlUserContext graphQlUserContext,
        RtQuery rtQuery, ITenantRepository tenantRepository, List<RtQueryRowDto> inputObjects,
        IOctoSessionAccessor sessionAccessor)
    {
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
                throw NavigationPropertyException.NavigationWithoutRestrictionNotAllowed(navigationPair.CkRoleId,
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

        return navigationPairToInputObjects;
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
                    ckCacheService.GetRtCkType(tenantRepository.TenantId, navigationPair.TargetCkTypeId);
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
                                                               throw NavigationPropertyException
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
                            throw NavigationPropertyException.AttributeNotFound(subAttributePath.ToPascalCase(),
                                navigationPair.TargetCkTypeId);
                    }
                }

                if (attributeGraph != null && attributeValueType == AttributeValueTypesDto.String)
                {
                    values = values.Select(v => v.ToString() ?? throw NavigationPropertyException
                        .CannotConvertValueToString(v));
                }

                navigationPair.FieldIn(subAttributePath, values);
            }
        }
    }
}