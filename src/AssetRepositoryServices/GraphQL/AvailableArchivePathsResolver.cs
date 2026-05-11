using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
/// Walks the CK type/record graph from <c>ckTypeId</c> and emits one
/// <see cref="ArchivePathInfoDto"/> per reachable attribute path. Backs the
/// <c>availableArchivePaths</c> GraphQL query (concept §16). Bounded by <c>maxDepth</c> to keep
/// recursive record structures finite.
/// </summary>
internal static class AvailableArchivePathsResolver
{
    public static IReadOnlyList<ArchivePathInfoDto> Resolve(
        ICkCacheService ckCache, string tenantId, RtCkId<CkTypeId> ckTypeId, int maxDepth)
    {
        if (maxDepth < 1) maxDepth = 1;

        var ckType = ckCache.GetRtCkType(tenantId, ckTypeId);

        var results = new List<ArchivePathInfoDto>();
        var visitedRecords = new HashSet<CkId<CkRecordId>>();

        foreach (var (name, attribute) in ckType.AllAttributesByName)
        {
            Walk(ckCache, tenantId, ckType.CkTypeId, name, attribute, isInsideArray: false,
                depth: 1, maxDepth, visitedRecords, results);
        }

        return results;
    }

    private static void Walk(
        ICkCacheService ckCache,
        string tenantId,
        CkId<CkTypeId> rootCkTypeId,
        string path,
        CkTypeAttributeGraph attribute,
        bool isInsideArray,
        int depth,
        int maxDepth,
        HashSet<CkId<CkRecordId>> visitedRecords,
        List<ArchivePathInfoDto> sink)
    {
        var isArray = isInsideArray
            || attribute.ValueType is AttributeValueTypesDto.RecordArray
            or AttributeValueTypesDto.StringArray
            or AttributeValueTypesDto.IntegerArray;

        if (attribute.ValueType is AttributeValueTypesDto.Record or AttributeValueTypesDto.RecordArray)
        {
            var recordId = attribute.ValueCkRecordId;
            sink.Add(new ArchivePathInfoDto(
                Path: path,
                PrimitiveType: null,
                IsRecord: true,
                IsArray: isArray,
                RecordTypeId: recordId,
                InheritedFromCkTypeId: null));

            if (recordId == null || depth >= maxDepth || !visitedRecords.Add(recordId))
            {
                return;
            }

            if (!ckCache.TryGetCkRecord(tenantId, recordId, out var recordGraph) || recordGraph == null)
            {
                visitedRecords.Remove(recordId);
                return;
            }

            foreach (var (childName, childAttribute) in recordGraph.AllAttributesByName)
            {
                Walk(ckCache, tenantId, rootCkTypeId, $"{path}.{childName}", childAttribute,
                    isInsideArray: isArray, depth + 1, maxDepth, visitedRecords, sink);
            }

            visitedRecords.Remove(recordId);
            return;
        }

        sink.Add(new ArchivePathInfoDto(
            Path: path,
            PrimitiveType: attribute.ValueType,
            IsRecord: false,
            IsArray: isArray,
            RecordTypeId: null,
            InheritedFromCkTypeId: null));
    }
}
