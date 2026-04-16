using Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal static class StreamDataFieldResolverExtensions
{
    /// <summary>
    /// Resolves each flat attribute name to a canonical+wire pair for cells-based
    /// stream-data resolvers. Null-forgives the resolver result because callers
    /// are expected to have validated column paths beforehand via
    /// StreamDataFieldValidation.
    /// </summary>
    public static IReadOnlyList<ColumnNameMapping> ResolveToMappings(
        this StreamDataFieldResolver resolver,
        IEnumerable<string> columns)
    {
        return columns.Select(c =>
        {
            var r = resolver.Resolve(c)!;
            return new ColumnNameMapping(r.CrateDbName, r.GraphQlAlias);
        }).ToList();
    }
}
