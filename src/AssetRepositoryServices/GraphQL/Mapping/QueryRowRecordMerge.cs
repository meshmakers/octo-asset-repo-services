using System.Collections;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Mapping;

/// <summary>
///     AB#8322: a query-row update writes record members path by path (<c>detail.label</c>, <c>detail.inner.text</c>,
///     <c>entries[0].label</c>). The update document holds only the written top-level attributes and the repository sets
///     each of them as a whole, so a record built from the cells alone would replace the stored record and drop every
///     member the row does not carry - also the <c>MethodOnly</c>, <c>Hidden</c> and (on update) <c>ReadOnly</c> members
///     the caller can neither see nor write. Before the cells are applied, the stored value of every record or record
///     array attribute that a cell writes a member of is therefore copied into the update document; the cells then
///     change exactly their path:
///     <list type="bullet">
///         <item>a single record keeps all other members, protected or not;</item>
///         <item>
///             a record array element is addressed by its position (<c>entries[0].label</c>) and changed in place, so it
///             keeps its other members whether or not its record declares a record key; the other elements stay as stored;
///         </item>
///         <item>nested records follow the same rule.</item>
///     </list>
///     <c>Secret</c> members are left out of the copy, as on every other update: the engine carries them over itself
///     (AB#5533). A cell that writes the whole record attribute (no member path) is not merged. The stored entity is read
///     with the request session (the caller's data permissions, inside the request transaction); an entity the caller
///     cannot see is not merged.
/// </summary>
internal static class QueryRowRecordMerge
{
    /// <summary>
    ///     Names (as in <see cref="CkTypeWithAttributesGraph.AllAttributesByName" />) of the record and record array
    ///     attributes of which at least one path writes a member. Navigation paths (<c>-&gt;</c>, <c>::</c>) and paths that
    ///     name the record attribute itself are ignored.
    /// </summary>
    public static IReadOnlyCollection<string> RecordAttributesWithMemberPaths(CkTypeWithAttributesGraph typeGraph,
        IEnumerable<string?> attributePaths)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in attributePaths)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Contains("->", StringComparison.Ordinal) ||
                path.Contains("::", StringComparison.Ordinal))
            {
                continue;
            }

            var end = path.IndexOfAny(['.', '[']);
            if (end <= 0)
            {
                // The whole attribute (or an empty first segment): nothing to merge.
                continue;
            }

            if (typeGraph.AllAttributesByName.TryGetValue(path[..end].ToPascalCase(), out var attribute) &&
                attribute.ValueType is AttributeValueTypesDto.Record or AttributeValueTypesDto.RecordArray)
            {
                result.Add(attribute.AttributeName);
            }
        }

        return result;
    }

    /// <summary>
    ///     Copies the stored values of <paramref name="attributeNames" /> into <paramref name="document" />, without their
    ///     Secret members (at any depth). Attributes without a stored value are left out.
    /// </summary>
    public static void CopyStoredRecords(ICkCacheService ckCacheService, string tenantId,
        CkTypeWithAttributesGraph typeGraph, RtEntity document, RtEntity? stored,
        IReadOnlyCollection<string> attributeNames)
    {
        if (stored == null)
        {
            return;
        }

        foreach (var name in attributeNames)
        {
            if (!typeGraph.AllAttributesByName.TryGetValue(name, out var attribute) ||
                !stored.Attributes.TryGetValue(name, out var storedValue) || storedValue == null)
            {
                continue;
            }

            var copy = CopyRecordValue(ckCacheService, tenantId, attribute, storedValue);
            if (copy != null)
            {
                document.SetAttributeRawValue(name, copy);
            }
        }
    }

    private static object? CopyRecordValue(ICkCacheService ckCacheService, string tenantId,
        CkTypeAttributeGraph attribute, object value)
    {
        if (attribute.ValueType == AttributeValueTypesDto.Record)
        {
            return value is RtRecord record ? CopyRecord(ckCacheService, tenantId, attribute, record) : null;
        }

        if (value is not IEnumerable elements || value is string)
        {
            return null;
        }

        var list = new List<object?>();
        foreach (var element in elements)
        {
            if (element is not RtRecord record)
            {
                // Not a record as the model declares it: do not merge this attribute at all.
                return null;
            }

            list.Add(CopyRecord(ckCacheService, tenantId, attribute, record));
        }

        return list;
    }

    private static RtRecord CopyRecord(ICkCacheService ckCacheService, string tenantId,
        CkTypeAttributeGraph attribute, RtRecord record)
    {
        var recordGraph = ResolveRecord(ckCacheService, tenantId, record, attribute);
        var members = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, memberValue) in record.Attributes)
        {
            CkTypeAttributeGraph? member = null;
            recordGraph?.AllAttributesByName.TryGetValue(name, out member);
            if (member?.ValueType == AttributeValueTypesDto.Secret)
            {
                // The engine carries Secret members over itself (AB#5533).
                continue;
            }

            if (member != null && memberValue != null &&
                member.ValueType is AttributeValueTypesDto.Record or AttributeValueTypesDto.RecordArray)
            {
                members[name] = CopyRecordValue(ckCacheService, tenantId, member, memberValue) ?? memberValue;
                continue;
            }

            members[name] = memberValue;
        }

        return new RtRecord(record.CkRecordId, members);
    }

    private static CkRecordGraph? ResolveRecord(ICkCacheService ckCacheService, string tenantId, RtRecord record,
        CkTypeAttributeGraph attribute)
    {
        // The element's own record id first: a record attribute may hold a derived record.
        if (record.CkRecordId is { IsEmpty: false } &&
            ckCacheService.TryGetRtCkRecord(tenantId, record.CkRecordId, out var graph))
        {
            return graph;
        }

        return attribute.ValueCkRecordId != null &&
               ckCacheService.TryGetCkRecord(tenantId, attribute.ValueCkRecordId, out var declared)
            ? declared
            : null;
    }
}
