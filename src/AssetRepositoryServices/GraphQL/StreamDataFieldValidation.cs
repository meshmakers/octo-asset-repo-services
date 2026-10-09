using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
/// Validates that attribute paths used in stream data queries refer to known fields
/// (either default system fields or CK-model data stream attributes).
/// </summary>
internal static class StreamDataFieldValidation
{
    /// <summary>
    /// Throws <see cref="OctoGraphQLException"/> if any of the given field names
    /// cannot be resolved by the field resolver.
    /// </summary>
    public static void ValidateStreamDataFields(
        StreamDataFieldResolver fieldResolver,
        IEnumerable<string>? columnNames,
        IEnumerable<string>? sortFieldNames,
        IEnumerable<string>? filterFieldNames,
        StreamDataHiddenGuard hiddenGuard)
    {
        var unknownFields = new List<string>();

        foreach (var group in new[] { columnNames, sortFieldNames, filterFieldNames })
        {
            if (group == null) continue;
            foreach (var name in group)
            {
                // CK v2 (review G3 E-M2): a stream-data path must not reach a Hidden attribute - checked first, so
                // the caller gets ATTRIBUTE_NOT_QUERYABLE and not "unknown column".
                hiddenGuard.EnsureNotHidden(name);

                if (fieldResolver.Resolve(name) == null)
                {
                    unknownFields.Add(name);
                }
            }
        }

        if (unknownFields.Count > 0)
        {
            throw OctoGraphQLException.InvalidColumnPaths(unknownFields);
        }
    }
}

/// <summary>
///     CK v2 (review G3 E-M2): refuses stream-data query paths (columns, group-by, sort, filters, aggregations) that reach
///     a Hidden attribute of the archive's target type, using the engine's
///     <see cref="ArchiveHiddenColumnGuard.FindHiddenAttribute" /> (a Hidden segment, or a whole-record column whose
///     record transitively contains a Hidden sub-attribute). Same error as the runtime guards:
///     <c>ATTRIBUTE_NOT_QUERYABLE</c>.
/// </summary>
internal sealed class StreamDataHiddenGuard(
    Meshmakers.Octo.ConstructionKit.Contracts.Services.ICkCacheService ckCacheService,
    string tenantId,
    RtCkId<CkTypeId> targetCkTypeId)
{
    public static StreamDataHiddenGuard For(IResolveFieldContext context, string tenantId,
        RtCkId<CkTypeId> targetCkTypeId)
    {
        return new StreamDataHiddenGuard(context.GetCkCacheService(), tenantId, targetCkTypeId);
    }

    public void EnsureNotHidden(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (ArchiveHiddenColumnGuard.FindHiddenAttribute(ckCacheService, tenantId, targetCkTypeId, path) != null)
        {
            throw HiddenAttributeAccessException.NotQueryable(path, targetCkTypeId.ToString(), "stream-data query");
        }
    }

    public void EnsureNotHidden(IEnumerable<string>? paths)
    {
        foreach (var path in paths ?? [])
        {
            EnsureNotHidden(path);
        }
    }
}
