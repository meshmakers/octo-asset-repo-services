using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Mapping;

internal class QueryMapper(
    IOctoSessionAccessor sessionAccessor,
    ICkCacheService ckCacheService,
    ITenantRepository tenantRepository,
    string tenantId,
    Dictionary<NavigationPair, List<RtEntityGraphItem>> navigationCache)
{
    public async Task MapAsync(RtEntity rtEntity, RtQueryRowDto queryRowDto,
        List<AssociationUpdateInfo> associationUpdateInfoList, MappingMode mappingMode, MappingResult mappingResult)
    {
        foreach (var navigationPairToInputObject in navigationCache)
        {
            var candidates = CompareNavigationProperties(navigationPairToInputObject.Key,
                new List<PathTerm>(), queryRowDto, navigationPairToInputObject.Value,
                mappingResult);

            if (candidates.Any())
            {
                var targetRtEntity = candidates.First();

                if (mappingMode == MappingMode.Update)
                {
                    var ckAssociationRoleGraph =
                        ckCacheService.GetCkAssociationRole(tenantId, navigationPairToInputObject.Key.CkRoleId);

                    if (navigationPairToInputObject.Key.Direction == GraphDirections.Outbound &&
                        ckAssociationRoleGraph.OutboundMultiplicity == MultiplicitiesDto.One ||
                        ckAssociationRoleGraph.OutboundMultiplicity == MultiplicitiesDto.ZeroOrOne)
                    {
                        var dataQueryOperation = DataQueryOperation.Create();
                        var associations = await tenantRepository.GetRtAssociationTargetsAsync(sessionAccessor.Session,
                            rtEntity.RtId, rtEntity.CkTypeId ?? throw OctoGraphQLException.CkTypeIdUndefined(),
                            navigationPairToInputObject.Key.CkRoleId,
                            navigationPairToInputObject.Key.TargetCkTypeId,
                            navigationPairToInputObject.Key.Direction,
                            null,
                            dataQueryOperation
                        );

                        if (associations.Items.Any())
                        {
                            foreach (var associationsItem in associations.Items)
                            {
                                if (associationsItem.RtId != targetRtEntity.RtId)
                                {
                                    associationUpdateInfoList.Add(AssociationUpdateInfo.CreateDelete(
                                        rtEntity.ToRtEntityId(), associationsItem.ToRtEntityId(),
                                        navigationPairToInputObject.Key.CkRoleId));
                                    associationUpdateInfoList.Add(AssociationUpdateInfo.CreateCreate(
                                        rtEntity.ToRtEntityId(), targetRtEntity.ToRtEntityId(),
                                        navigationPairToInputObject.Key.CkRoleId));
                                }
                            }
                        }
                    }
                    else if (navigationPairToInputObject.Key.Direction == GraphDirections.Inbound &&
                             ckAssociationRoleGraph.InboundMultiplicity == MultiplicitiesDto.One ||
                             ckAssociationRoleGraph.InboundMultiplicity == MultiplicitiesDto.ZeroOrOne)
                    {
                        var dataQueryOperation = DataQueryOperation.Create();
                        var associations = await tenantRepository.GetRtAssociationTargetsAsync(sessionAccessor.Session,
                            rtEntity.RtId, rtEntity.CkTypeId ?? throw OctoGraphQLException.CkTypeIdUndefined(),
                            navigationPairToInputObject.Key.CkRoleId,
                            navigationPairToInputObject.Key.TargetCkTypeId,
                            navigationPairToInputObject.Key.Direction,
                            null,
                            dataQueryOperation
                        );

                        if (associations.Items.Any())
                        {
                            foreach (var associationsItem in associations.Items)
                            {
                                associationUpdateInfoList.Add(AssociationUpdateInfo.CreateDelete(
                                    associationsItem.ToRtEntityId(),rtEntity.ToRtEntityId(),
                                    navigationPairToInputObject.Key.CkRoleId));
                                associationUpdateInfoList.Add(AssociationUpdateInfo.CreateCreate(
                                    targetRtEntity.ToRtEntityId(), rtEntity.ToRtEntityId(),
                                    navigationPairToInputObject.Key.CkRoleId));
                            }
                        }
                    }
                }
                else // On creation, add the association
                {
                    if (navigationPairToInputObject.Key.Direction == GraphDirections.Outbound)
                    {
                        associationUpdateInfoList.Add(AssociationUpdateInfo.CreateCreate(
                            rtEntity.ToRtEntityId(), targetRtEntity.ToRtEntityId(),
                            navigationPairToInputObject.Key.CkRoleId));
                    }
                    else if (navigationPairToInputObject.Key.Direction == GraphDirections.Inbound)
                    {
                        associationUpdateInfoList.Add(AssociationUpdateInfo.CreateCreate(
                            targetRtEntity.ToRtEntityId(), rtEntity.ToRtEntityId(),
                            navigationPairToInputObject.Key.CkRoleId));
                    }
                }
            }
        }

        RtEntityFromInputObject(rtEntity, queryRowDto, mappingResult);
    }

    private List<RtEntityGraphItem> CompareNavigationProperties(NavigationPair navigationPair,
        List<PathTerm> parentPathTerms,
        RtQueryRowDto queryRowDto,
        List<RtEntityGraphItem> candidates,
        MappingResult mappingResult)
    {
        var subPathTerms =
            navigationPair.SubPathTerms.Where(pt => pt.First().Type == PathType.Attribute)
                .ToArray();

        var pathTerms = subPathTerms.Select(spt => navigationPair.PathTerms.Concat(spt))
            .ToArray();
        var attributePaths = pathTerms.Select(RtPathEvaluator.GetPath);

        var cellDto = queryRowDto.Cells?.Where(c => attributePaths.Contains(c.AttributePath)).ToArray();
        if (cellDto != null && cellDto.Any())
        {
            var compareValues = cellDto.ToDictionary(k => k.AttributePath, v => v.Value);

            var subCandidates = candidates.Where(t => subPathTerms.All(spt =>
            {
                var path1 = spt.ToArray();
                var path2 = parentPathTerms.Concat(path1).ToArray();
                var compareValue = compareValues[RtPathEvaluator.GetPath(navigationPair.PathTerms.Concat(path1))];
                return t.GetAttributeValueByAccessPath(ckCacheService, tenantId, path2)?.ToString() ==
                       compareValue?.ToString();
            })).ToList();

            if (navigationPair.InnerNavigationPairs.Any())
            {
                foreach (var navigationPathTerms in
                         navigationPair.SubPathTerms.Where(pt => pt.First().Type == PathType.Navigation))
                {
                    var enumerable = navigationPathTerms as PathTerm[] ?? navigationPathTerms.ToArray();
                    var fullNavigationTerms =
                        RtPathEvaluator.GetPath(navigationPair.PathTerms.Concat(enumerable).SkipLast(1));
                    var innerNavigationPair = navigationPair.InnerNavigationPairs
                        .Single(np => RtPathEvaluator.GetPath(np.PathTerms) == fullNavigationTerms);

                    subCandidates = CompareNavigationProperties(innerNavigationPair, enumerable.SkipLast(1).ToList(),
                        queryRowDto, subCandidates, mappingResult);
                }
            }

            if (!subCandidates.Any())
            {
                mappingResult.Errors.Add(new NavigationPairMappingError
                {
                    Comparision = compareValues, ErrorId = "MAPPING001", NavigationPair = navigationPair,
                    ErrorMessage = "No suitable candidate is found, but a candidate is required",
                    Candidates = candidates
                });
            }
            else if (subCandidates.Count > 1)
            {
                mappingResult.Errors.Add(new NavigationPairMappingError
                {
                    Comparision = compareValues, ErrorId = "MAPPING002", NavigationPair = navigationPair,
                    ErrorMessage =
                        "Several suitable candidates are found, but only one candidate can be present for the assignment",
                    Candidates = candidates
                });
            }

            return subCandidates;
        }

        return new List<RtEntityGraphItem>();
    }

    private void RtEntityFromInputObject(RtEntity rtEntity,
        RtQueryRowDto rtQueryRowDto, MappingResult mappingResult)
    {
        rtEntity.RtWellKnownName = rtQueryRowDto.RtWellKnownName;

        if (rtQueryRowDto.Cells != null)
        {
            foreach (var cellDto in rtQueryRowDto.Cells)
            {
                // Ignore attribute paths that are navigation properties
                var navigationPair = RtPathEvaluator.TokenizeAndGetNavigationPair(ckCacheService, tenantId,
                    rtEntity.CkTypeId ?? throw OctoGraphQLException.CkTypeIdUndefined(), cellDto.AttributePath);
                if (navigationPair != null)
                {
                    continue;
                }

                try
                {
                    RtPathEvaluator.SetValue(ckCacheService, tenantId, rtEntity, cellDto.AttributePath, cellDto.Value);
                }
                catch (CkEnumValueNotFoundException ex)
                {
                    mappingResult.Errors.Add(new MappingError
                    {
                        Comparision = new Dictionary<string, object?>
                        {
                            { cellDto.AttributePath, cellDto.Value }
                        },
                        ErrorId = "MAPPING003",
                        ErrorMessage =
                            $"Enum value '{ex.EnumValue}' not found"
                    });
                }
                catch (InvalidPathException ex)
                {
                    mappingResult.Errors.Add(new MappingError
                    {
                        Comparision = new Dictionary<string, object?>
                        {
                            { cellDto.AttributePath, cellDto.Value }
                        },
                        ErrorId = "MAPPING004",
                        ErrorMessage = ex.Message
                    });
                }
                catch (Exception ex)
                {
                    mappingResult.Errors.Add(new MappingError
                    {
                        Comparision = new Dictionary<string, object?>
                        {
                            { cellDto.AttributePath, cellDto.Value }
                        },
                        ErrorId = "MAPPING005",
                        ErrorMessage = ex.Message
                    });
                }
            }
        }
    }
}