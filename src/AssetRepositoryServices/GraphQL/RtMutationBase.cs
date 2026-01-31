using GraphQL.Types;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

internal abstract class RtMutationBase : ObjectGraphType
{
    protected async Task RtEntityFromInputObjectAsync(ICkCacheService ckCacheService, string tenantId,
        RtEntity rtEntity,
        RtEntityDto rtEntityDto,
        List<AssociationUpdateInfo> associations)
    {
        var ckTypeGraph =
            ckCacheService.GetRtCkType(tenantId, rtEntity.CkTypeId ?? throw OctoGraphQLException.CkTypeIdUndefined());

        rtEntity.RtWellKnownName = rtEntityDto.RtWellKnownName;

        if (rtEntityDto.Attributes != null)
        {
            foreach (var item in rtEntityDto.Attributes)
            {
                if (await TryHandleAttributeAsync(ckCacheService, tenantId, rtEntity, ckTypeGraph,
                        item.AttributeName.ToPascalCase(), item.Value))
                {
                    continue;
                }

                if (TryHandleInboundAssoc(rtEntity, ckTypeGraph, item, associations))
                {
                    continue;
                }

                TryHandleOutboundAssoc(rtEntity, ckTypeGraph, item, associations);
            }
        }
    }

    protected async Task<IEnumerable<RtEntityDto>> GetResultSet(IOctoSession session, ITenantRepository repository,
        List<EntityUpdateInfo<RtEntity>> entityUpdateInfos)
    {
        var resultSetComplete = new List<RtEntity>();
        foreach (var grouping in entityUpdateInfos.GroupBy(x => x.CkTypeId))
        {
            var resultSet = await repository.GetRtEntitiesByIdAsync(session, grouping.Key,
                entityUpdateInfos.Select(x => x.RtId ?? throw OctoGraphQLException.RtIdUndefined()
                ).ToList(), RtEntityQueryOptions.Create());

            resultSetComplete.AddRange(resultSet.Items);
        }


        return resultSetComplete.Select(RtEntityDtoType.CreateRtEntityDto);
    }

    protected async Task<IEnumerable<RtSimpleQueryRowDto>> GetRtQueryRowResultSet(IOctoSession session,
        ICkCacheService ckCacheService, ITenantRepository repository,
        List<EntityUpdateInfo<RtEntity>> entityUpdateInfos, OctoObjectId queryRtId)
    {
        var rtQuery =
            await repository.GetRtEntityByRtIdAsync<RtSimpleRtQuery>(session,
                queryRtId);
        if (rtQuery == null)
        {
            throw OctoGraphQLException.RtQueryNotFound(queryRtId);
        }

        var resultSetComplete = new List<RtEntityGraphItem>();
        foreach (var grouping in entityUpdateInfos.GroupBy(x => x.CkTypeId))
        {
            var roleIdDirectionPairs = RtPathEvaluator.TokenizeAndGetNavigationPairsByRtCkId(ckCacheService,
                repository.TenantId, rtQuery.QueryCkTypeId,
                rtQuery.Columns.Select(column => column));

            var resultSet = await repository.GetRtEntitiesGraphByIdAsync(session, grouping.Key,
                entityUpdateInfos.Select(x => x.RtId ?? throw OctoGraphQLException.RtIdUndefined()
                ).ToList(), RtEntityQueryOptions.Create(), roleIdDirectionPairs);

            resultSetComplete.AddRange(resultSet.Items);
        }

        var typeQueryColumnPaths = ckCacheService.GetCkTypeQueryColumnPathsByRtCkId(repository.TenantId, rtQuery.QueryCkTypeId);
        var invalidColumnPaths = rtQuery.Columns
            .Where(cp => typeQueryColumnPaths.All(ckTypeQueryColumn => ckTypeQueryColumn.Path != cp)).ToList();
        if (invalidColumnPaths.Any())
        {
            throw OctoGraphQLException.InvalidColumnPaths(invalidColumnPaths);
        }

        var selectedTypeQueryColumns = typeQueryColumnPaths
            .Where(ckTypeQueryColumn => rtQuery.Columns.Contains(ckTypeQueryColumn.Path))
            .Select(ckTypeQueryColumn => Tuple.Create(ckTypeQueryColumn, AggregationTypesDto.None))
            .ToList();

        return resultSetComplete.Select((entity, _) =>
            RtSimpleQueryRowDtoType.CreateRtQueryRowDto(repository.TenantId, entity, selectedTypeQueryColumns));
    }


    private async Task<bool> TryHandleAttributeAsync(ICkCacheService ckCacheService, string tenantId,
        RtTypeWithAttributes rtTypeWithAttributes,
        CkTypeWithAttributesGraph ckTypeWithAttributesGraph, string attributeName,
        object? value)
    {
        if (ckTypeWithAttributesGraph.AllAttributesByName.TryGetValue(attributeName, out var ckTypeAttributeGraph))
        {
            switch (ckTypeAttributeGraph.ValueType)
            {
                case AttributeValueTypesDto.Record:
                    if (ckTypeAttributeGraph.ValueCkRecordId == null)
                    {
                        throw OctoGraphQLException.CkRecordIdUndefined();
                    }

                    if (value == null)
                    {
                        rtTypeWithAttributes.SetAttributeValue(ckTypeAttributeGraph.AttributeName,
                            ckTypeAttributeGraph.ValueType, null);
                        return true;
                    }

                    var rtRecordDto = ConvertToRtRecordDto(value, ckTypeAttributeGraph.ValueCkRecordId.ToRtCkId());
                    var rtRecord = await HandleRecordAsync(ckCacheService, tenantId,
                        ckTypeAttributeGraph.ValueCkRecordId,
                        rtRecordDto);
                    rtTypeWithAttributes.SetAttributeValue(ckTypeAttributeGraph.AttributeName,
                        ckTypeAttributeGraph.ValueType, rtRecord);
                    return true;

                case AttributeValueTypesDto.RecordArray:
                    if (ckTypeAttributeGraph.ValueCkRecordId == null)
                    {
                        throw OctoGraphQLException.CkRecordIdUndefined();
                    }

                    if (value == null)
                    {
                        rtTypeWithAttributes.SetAttributeValue(ckTypeAttributeGraph.AttributeName,
                            ckTypeAttributeGraph.ValueType, null);
                        return true;
                    }

                    var rtRecordList = (IEnumerable<object>)value;
                    AttributeRecordValueList<RtRecord> rtRecords = new();
                    foreach (var rtRecordItem in rtRecordList)
                    {
                        var rtRecordDtoItem = ConvertToRtRecordDto(rtRecordItem, ckTypeAttributeGraph.ValueCkRecordId.ToRtCkId());
                        var rtRecordItemResult = await HandleRecordAsync(ckCacheService, tenantId,
                            ckTypeAttributeGraph.ValueCkRecordId,
                            rtRecordDtoItem);
                        rtRecords.Add(rtRecordItemResult);
                    }

                    rtTypeWithAttributes.SetAttributeValue(ckTypeAttributeGraph.AttributeName,
                        ckTypeAttributeGraph.ValueType, rtRecords);
                    return true;
                case AttributeValueTypesDto.BinaryLinked:
                    if (value is IFormFile file)
                    {
                        var entityBinaryInfo = new EntityBinaryInfo
                        {
                            ContentType = file.ContentType,
                            Filename = file.FileName,
                            Size = file.Length,
                            Stream = file.OpenReadStream()
                        };
                        rtTypeWithAttributes.SetAttributeValue(ckTypeAttributeGraph.AttributeName,
                            ckTypeAttributeGraph.ValueType, entityBinaryInfo);
                    }

                    return true;
                case AttributeValueTypesDto.Binary:
                    // Convert incoming data to byte[] for proper storage
                    var binaryData = ConvertToBinaryData(value);
                    rtTypeWithAttributes.SetAttributeValue(ckTypeAttributeGraph.AttributeName,
                        ckTypeAttributeGraph.ValueType, binaryData);
                    return true;
                default:
                    rtTypeWithAttributes.SetAttributeValue(ckTypeAttributeGraph.AttributeName,
                        ckTypeAttributeGraph.ValueType, value);
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Converts a value to RtRecordDto. Handles both RtRecordDto instances and Dictionary&lt;string, object&gt; from GraphQL input.
    /// </summary>
    private static RtRecordDto ConvertToRtRecordDto(object value, RtCkId<CkRecordId> ckRecordId)
    {
        // If already an RtRecordDto, return it directly
        if (value is RtRecordDto rtRecordDto)
        {
            return rtRecordDto;
        }

        // Handle Dictionary from generic GraphQL input (when using RtEntityAttributeDtoInputType with SimpleScalarType)
        if (value is IDictionary<string, object> dictionary)
        {
            var dto = new RtRecordDto
            {
                CkRecordId = ckRecordId,
                Attributes = new List<RtEntityAttributeDto>()
            };

            foreach (var (key, val) in dictionary)
            {
                dto.Attributes.Add(new RtEntityAttributeDto
                {
                    AttributeName = key,
                    Value = val
                });
            }

            return dto;
        }

        throw new InvalidCastException(
            $"Unable to convert value of type '{value.GetType().FullName}' to RtRecordDto. " +
            $"Expected either RtRecordDto or Dictionary<string, object>.");
    }

    /// <summary>
    /// Converts incoming GraphQL value to byte[] for Binary attributes.
    /// Handles byte[], List&lt;object&gt; (from JSON arrays), and other enumerable formats.
    /// </summary>
    private static byte[]? ConvertToBinaryData(object? value)
    {
        if (value == null)
        {
            return null;
        }

        // Already a byte array
        if (value is byte[] byteArray)
        {
            return byteArray;
        }

        // Handle List<object> or other IEnumerable from GraphQL JSON input
        if (value is IEnumerable<object> objectList)
        {
            return objectList.Select(item => Convert.ToByte(item)).ToArray();
        }

        // Handle generic IEnumerable
        if (value is System.Collections.IEnumerable enumerable)
        {
            var bytes = new List<byte>();
            foreach (var item in enumerable)
            {
                bytes.Add(Convert.ToByte(item));
            }
            return bytes.ToArray();
        }

        throw new InvalidCastException(
            $"Unable to convert value of type '{value.GetType().FullName}' to byte[]. " +
            $"Expected byte[], List<object>, or IEnumerable.");
    }

    private async Task<RtRecord> HandleRecordAsync(ICkCacheService ckCacheService, string tenantId,
        CkId<CkRecordId> ckRecordId,
        RtRecordDto rtRecordDto)
    {
        var ckRecordGraph = ckCacheService.GetCkRecord(tenantId, ckRecordId);

        var rtRecord = new RtRecord
        {
            CkRecordId = ckRecordGraph.CkRecordId.ToRtCkId()
        };

        if (rtRecordDto.Attributes != null)
        {
            foreach (var recordAttributeItem in rtRecordDto.Attributes)
            {
                await TryHandleAttributeAsync(ckCacheService, tenantId, rtRecord, ckRecordGraph,
                    recordAttributeItem.AttributeName.ToPascalCase(), recordAttributeItem.Value);
            }
        }

        return rtRecord;
    }

    // ReSharper disable once UnusedMethodReturnValue.Local
    private bool TryHandleInboundAssoc(RtEntity rtEntity, CkTypeGraph ckTypeGraph,
        RtEntityAttributeDto item, List<AssociationUpdateInfo> associations)
    {
        var assocName = item.AttributeName.ToPascalCase();

        var ckTypeAssociationGraph = ckTypeGraph.Associations.In.All
            .FirstOrDefault(a => a.NavigationPropertyName == assocName);
        if (ckTypeAssociationGraph == null)
        {
            return false;
        }

        if (item.Value != null)
        {
            var rtAssociationInputDtos = (IEnumerable<object>)item.Value;
            foreach (RtAssociationInputDto rtAssociationDto in rtAssociationInputDtos)
            {
                var assocInfo = new AssociationUpdateInfo(
                    new RtEntityId(rtAssociationDto.Target.CkTypeId, rtAssociationDto.Target.RtId),
                    rtEntity.ToRtEntityId(),
                    ckTypeAssociationGraph.CkRoleId.ToRtCkId(),
                    rtAssociationDto.ModOption ?? AssociationModOptionsDto.Create);
                associations.Add(assocInfo);
            }
        }

        return true;
    }

    // ReSharper disable once UnusedMethodReturnValue.Local
    private bool TryHandleOutboundAssoc(RtEntity rtEntity, CkTypeGraph ckTypeGraph,
        RtEntityAttributeDto item, List<AssociationUpdateInfo> associations)
    {
        var assocName = item.AttributeName.ToPascalCase();

        var typeAssociationGraph = ckTypeGraph.Associations.Out.All
            .FirstOrDefault(a => a.NavigationPropertyName == assocName);
        if (typeAssociationGraph == null)
        {
            return false;
        }

        if (item.Value != null)
        {
            var rtAssociationInputDtos = (IEnumerable<object>)item.Value;
            foreach (RtAssociationInputDto rtAssociationDto in rtAssociationInputDtos)
            {
                var assocInfo = new AssociationUpdateInfo(
                    rtEntity.ToRtEntityId(),
                    new RtEntityId(rtAssociationDto.Target.CkTypeId, rtAssociationDto.Target.RtId),
                    typeAssociationGraph.CkRoleId.ToRtCkId(),
                    rtAssociationDto.ModOption ?? AssociationModOptionsDto.Create);
                associations.Add(assocInfo);
            }
        }

        return true;
    }
}