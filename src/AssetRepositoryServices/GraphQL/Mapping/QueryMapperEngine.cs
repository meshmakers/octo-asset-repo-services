using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Mapping;

internal class QueryMapperEngine
{


    internal async Task<QueryMapper> CreateQueryMapperAsync(ICkCacheService ckCacheService, GraphQlUserContext graphQlUserContext,
        RtSimpleRtQuery rtQuery, ITenantRepository tenantRepository, List<RtSimpleQueryRowDto> inputObjects,
        IOctoSessionAccessor sessionAccessor)
    {
        var navigationPairToInputObjects = await NavigationPairToInputObjects(ckCacheService, graphQlUserContext,
            rtQuery, tenantRepository, inputObjects, sessionAccessor);
        return new QueryMapper(sessionAccessor, ckCacheService, tenantRepository, graphQlUserContext.TenantId, navigationPairToInputObjects);
    }

    private async Task<Dictionary<NavigationPair, List<RtEntityGraphItem>>> NavigationPairToInputObjects(
        ICkCacheService ckCacheService, GraphQlUserContext graphQlUserContext,
        RtSimpleRtQuery rtQuery, ITenantRepository tenantRepository, List<RtSimpleQueryRowDto> inputObjects,
        IOctoSessionAccessor sessionAccessor)
    {
        // Re-review N1: stored columns (incl. selector keys) are validated before their selectors become filters.
        AccessQueryGuard.EnsureColumnPathsAllowed(ckCacheService, graphQlUserContext.TenantId,
            rtQuery.QueryCkTypeId, rtQuery.Columns.ToList());

        var navigationPairs = RtPathEvaluator.TokenizeAndGetNavigationPairsByRtCkId(ckCacheService, graphQlUserContext.TenantId,
            rtQuery.QueryCkTypeId,
            rtQuery.Columns);

        await EvaluateNavigationFilters(ckCacheService, tenantRepository, navigationPairs, inputObjects);

        // Find results
        Dictionary<NavigationPair, List<RtEntityGraphItem>> navigationPairToInputObjects = new();
        foreach (var navigationPair in navigationPairs)
        {
            var queryOptions = RtEntityQueryOptions.Create();
            if (navigationPair.FieldFilters == null || !navigationPair.FieldFilters.Any())
            {
                throw NavigationPropertyException.NavigationWithoutRestrictionNotAllowed(navigationPair.CkRoleId,
                    navigationPair.Direction, navigationPair.TargetCkTypeId);
            }

            foreach (var navigationPairFieldFilter in navigationPair.FieldFilters)
            {
                queryOptions.AddFieldFilter(navigationPairFieldFilter.AttributePath,
                    navigationPairFieldFilter.Operator, navigationPairFieldFilter.ComparisonValue);
            }

            var resultSet = await tenantRepository.GetRtEntitiesGraphByTypeAsync(sessionAccessor.Session,
                navigationPair.TargetCkTypeId,
                queryOptions, navigationPair.InnerNavigationPairs);
            navigationPairToInputObjects.Add(navigationPair, resultSet.Items.ToList());
        }

        return navigationPairToInputObjects;
    }

    private async Task EvaluateNavigationFilters(ICkCacheService ckCacheService, ITenantRepository tenantRepository,
        List<NavigationPair> navigationPairs, List<RtSimpleQueryRowDto> inputObjects)
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
                if (AccessQueryGuard.IsHidden(attributeGraph))
                {
                    // CK v2 (AB#5668, review L10): a hidden attribute cannot identify a navigation target either
                    // (equality oracle).
                    throw HiddenAttributeAccessException.NotQueryable(subAttributePath, navigationPair.TargetCkTypeId.ToString(),
                        "navigation lookup");
                }

                if (attributeValueType == AttributeValueTypesDto.Secret)
                {
                    // AB#5528: a secret cannot identify a navigation target (that would be an equality
                    // lookup on the credential).
                    throw new SecretAttributeNotQueryableException(subAttributePath, "navigation lookup",
                        navigationPair.TargetCkTypeId.ToString());
                }

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
                        case nameof(RtEntity.RtCreatedBy):
                        case nameof(RtEntity.RtDisplayName):
                        case nameof(RtEntity.RtDisplayDescription):
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