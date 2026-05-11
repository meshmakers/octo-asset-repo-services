using Meshmakers.Octo.Runtime.Engine.CrateDb;

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
        IEnumerable<string>? filterFieldNames)
    {
        var unknownFields = new List<string>();

        foreach (var group in new[] { columnNames, sortFieldNames, filterFieldNames })
        {
            if (group == null) continue;
            foreach (var name in group)
            {
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
