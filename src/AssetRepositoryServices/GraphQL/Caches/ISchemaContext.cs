using GraphQL.Types;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;

public interface ISchemaContext
{
    /// <summary>
    ///     Invalidates a cached schema
    /// </summary>
    /// <param name="tenantId">The Id of tenant</param>
    void Invalidate(string tenantId);

    /// <summary>
    ///     Creates or gets a schema
    /// </summary>
    /// <param name="tenantId">The Id of tenant</param>
    /// <returns>The corresponding schema</returns>
    Task<ISchema> GetOrCreateAsync(string tenantId);
}
