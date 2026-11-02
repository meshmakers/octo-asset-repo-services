using System.Collections;
using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     AB#8055: a whole-record write on update (typed or generic <c>update</c>) rebuilds the record from its input fields. A
///     record member the caller may not write on update (<c>Hidden</c>, <c>MethodOnly</c>, <c>ReadOnly</c>; the engine's
///     <see cref="AttributeAccess.IsGenericallyWritable" />) cannot be in that input - an explicit value is refused with
///     <c>ATTRIBUTE_NOT_WRITABLE</c> - so without this step the write would clear it. The stored value of every such member
///     is carried into the rebuilt record instead, the same rule as identity-owned attributes on <c>ImportRt</c> (AB#8059:
///     preserved on omission) and the same element matching as the engine's Secret carry-over (AB#5533):
///     <list type="bullet">
///         <item>a single record: by position (there is exactly one element);</item>
///         <item>
///             a record array: by the record key (<c>recordKey</c>) of the element; an element without a stored counterpart
///             is new and has nothing to keep;
///         </item>
///         <item>
///             a record array whose record declares no usable key (none, or the key member itself is not writable on
///             update): the elements cannot be matched, so the write is refused with <c>ATTRIBUTE_NOT_WRITABLE</c> when a
///             stored element holds a value in such a member; with nothing stored there, it is allowed.
///         </item>
///     </list>
///     Nested records follow the same rules. Secret members are left to the engine, which carries them over itself
///     (record key). Removing an element (or writing <c>null</c> for the whole record attribute) still removes its members,
///     as for Secret members.
/// </summary>
internal static class RecordMemberPreservation
{
    /// <summary>
    ///     True when the update writes a record (or record array) attribute whose record has, at any depth, a member that is
    ///     not writable on update - only then the stored entity is needed.
    /// </summary>
    public static bool NeedsStoredEntity(ICkCacheService ckCacheService, string tenantId,
        CkTypeWithAttributesGraph typeGraph, RtTypeWithAttributes incoming)
    {
        foreach (var (name, value) in incoming.Attributes)
        {
            if (value == null || !typeGraph.AllAttributesByName.TryGetValue(name, out var attribute))
            {
                continue;
            }

            if (attribute.ValueType is AttributeValueTypesDto.Record or AttributeValueTypesDto.RecordArray &&
                RecordHasProtectedMember(ckCacheService, tenantId, attribute, []))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Carries the stored values of protected record members into the records of <paramref name="incoming" />.
    /// </summary>
    /// <exception cref="HiddenAttributeAccessException">
    ///     A record array without a usable record key would drop a stored protected member value.
    /// </exception>
    public static void Apply(ICkCacheService ckCacheService, string tenantId, CkTypeWithAttributesGraph typeGraph,
        RtTypeWithAttributes incoming, RtTypeWithAttributes? stored)
    {
        foreach (var (name, value) in incoming.Attributes.ToList())
        {
            if (value == null || !typeGraph.AllAttributesByName.TryGetValue(name, out var attribute) ||
                attribute.ValueType is not (AttributeValueTypesDto.Record or AttributeValueTypesDto.RecordArray) ||
                !RecordHasProtectedMember(ckCacheService, tenantId, attribute, []))
            {
                continue;
            }

            object? storedValue = null;
            stored?.Attributes.TryGetValue(name, out storedValue);
            ApplyToRecordAttribute(ckCacheService, tenantId, attribute, value, storedValue);
        }
    }

    private static void ApplyToRecordAttribute(ICkCacheService ckCacheService, string tenantId,
        CkTypeAttributeGraph attribute, object incomingValue, object? storedValue)
    {
        if (attribute.ValueType == AttributeValueTypesDto.Record)
        {
            if (incomingValue is RtRecord incomingRecord &&
                ResolveRecord(ckCacheService, tenantId, incomingRecord, attribute) is { } recordGraph)
            {
                ApplyToRecord(ckCacheService, tenantId, recordGraph, incomingRecord, storedValue as RtRecord);
            }

            return;
        }

        if (incomingValue is not IEnumerable incomingElements || incomingValue is string)
        {
            return;
        }

        var storedElements = storedValue is IEnumerable storedEnumerable and not string
            ? storedEnumerable.OfType<RtRecord>().ToList()
            : [];

        foreach (var incomingRecord in incomingElements.OfType<RtRecord>())
        {
            var recordGraph = ResolveRecord(ckCacheService, tenantId, incomingRecord, attribute);
            if (recordGraph == null)
            {
                continue;
            }

            var recordKey = UsableRecordKey(recordGraph);
            if (recordKey == null)
            {
                EnsureNothingStoredToKeep(ckCacheService, tenantId, recordGraph, storedElements);
                continue;
            }

            ApplyToRecord(ckCacheService, tenantId, recordGraph, incomingRecord,
                FindByRecordKey(recordKey, incomingRecord, storedElements));
        }
    }

    private static void ApplyToRecord(ICkCacheService ckCacheService, string tenantId, CkRecordGraph recordGraph,
        RtRecord incoming, RtRecord? stored)
    {
        foreach (var member in recordGraph.AllAttributes.Values)
        {
            if (member.ValueType == AttributeValueTypesDto.Secret)
            {
                // The engine carries Secret members over itself (record key, AB#5533).
                continue;
            }

            if (!AttributeAccess.IsGenericallyWritable(member.Access, false))
            {
                // Not in the input (an explicit value was refused already): keep what is stored.
                if (stored != null && stored.Attributes.TryGetValue(member.AttributeName, out var storedMember) &&
                    storedMember != null)
                {
                    incoming.SetAttributeRawValue(member.AttributeName, storedMember);
                }

                continue;
            }

            if (member.ValueType is AttributeValueTypesDto.Record or AttributeValueTypesDto.RecordArray &&
                incoming.Attributes.TryGetValue(member.AttributeName, out var nestedValue) && nestedValue != null &&
                RecordHasProtectedMember(ckCacheService, tenantId, member, []))
            {
                object? storedNested = null;
                stored?.Attributes.TryGetValue(member.AttributeName, out storedNested);
                ApplyToRecordAttribute(ckCacheService, tenantId, member, nestedValue, storedNested);
            }
        }
    }

    /// <summary>
    ///     Without a usable record key the elements of a record array cannot be matched; refuse the write when it would drop
    ///     a stored value of a protected member.
    /// </summary>
    private static void EnsureNothingStoredToKeep(ICkCacheService ckCacheService, string tenantId,
        CkRecordGraph recordGraph, IReadOnlyList<RtRecord> storedElements)
    {
        foreach (var storedElement in storedElements)
        {
            foreach (var member in recordGraph.AllAttributes.Values)
            {
                if (member.ValueType == AttributeValueTypesDto.Secret ||
                    !storedElement.Attributes.TryGetValue(member.AttributeName, out var storedMember) ||
                    storedMember == null)
                {
                    continue;
                }

                if (!AttributeAccess.IsGenericallyWritable(member.Access, false))
                {
                    throw HiddenAttributeAccessException.NotWritable(member.AttributeName,
                        recordGraph.CkRecordId.ToString(), member.Access);
                }
            }
        }
    }

    private static bool RecordHasProtectedMember(ICkCacheService ckCacheService, string tenantId,
        CkTypeAttributeGraph attribute, HashSet<string> visited)
    {
        if (attribute.ValueCkRecordId == null ||
            !ckCacheService.TryGetCkRecord(tenantId, attribute.ValueCkRecordId, out var recordGraph) ||
            !visited.Add(recordGraph.CkRecordId.ToString()))
        {
            return false;
        }

        foreach (var member in recordGraph.AllAttributes.Values)
        {
            if (member.ValueType == AttributeValueTypesDto.Secret)
            {
                continue;
            }

            if (!AttributeAccess.IsGenericallyWritable(member.Access, false))
            {
                return true;
            }

            if (member.ValueType is AttributeValueTypesDto.Record or AttributeValueTypesDto.RecordArray &&
                RecordHasProtectedMember(ckCacheService, tenantId, member, visited))
            {
                return true;
            }
        }

        return false;
    }

    private static CkRecordGraph? ResolveRecord(ICkCacheService ckCacheService, string tenantId, RtRecord record,
        CkTypeAttributeGraph attribute)
    {
        // The element's own record id first: a record array may hold derived records.
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

    /// <summary>The record key, when it is declared and the caller may write it on update (else no matching).</summary>
    private static string? UsableRecordKey(CkRecordGraph recordGraph)
    {
        if (string.IsNullOrWhiteSpace(recordGraph.RecordKey) ||
            !recordGraph.AllAttributesByName.TryGetValue(recordGraph.RecordKey, out var keyAttribute))
        {
            return null;
        }

        return AttributeAccess.IsGenericallyWritable(keyAttribute.Access, false) ? recordGraph.RecordKey : null;
    }

    /// <summary>Finds the stored element with the same record key value (the engine's Secret carry-over rule).</summary>
    internal static RtRecord? FindByRecordKey(string recordKey, RtRecord incoming, IReadOnlyList<RtRecord> stored)
    {
        if (stored.Count == 0 || !incoming.Attributes.TryGetValue(recordKey, out var key) || key == null)
        {
            return null;
        }

        return stored.FirstOrDefault(candidate =>
            candidate.Attributes.TryGetValue(recordKey, out var candidateKey) && candidateKey != null &&
            RecordKeyEquals(key, candidateKey));
    }

    /// <summary>
    ///     Record keys compare by value: numbers across CLR types (int / long / enum key), everything else by its invariant
    ///     text, ordinal (as the engine's <c>SecretWriteNormalizer.RecordKeyEquals</c>).
    /// </summary>
    internal static bool RecordKeyEquals(object a, object b)
    {
        if (IsIntegral(a) && IsIntegral(b))
        {
            return Convert.ToDecimal(a, CultureInfo.InvariantCulture) ==
                   Convert.ToDecimal(b, CultureInfo.InvariantCulture);
        }

        return string.Equals(Convert.ToString(a, CultureInfo.InvariantCulture),
            Convert.ToString(b, CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static bool IsIntegral(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong or Enum;
}
