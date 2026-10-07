using Meshmakers.Octo.Backend.AssetRepositoryServices.Secrets;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Secrets;

/// <summary>
///     Source object of <c>secrets { … }</c>, created per request after the role check. Caches the secret usage
///     index so that <c>usedBy</c> of every inventory item and <c>usages</c> share one pipeline scan.
/// </summary>
internal sealed class SecretsRequestContext(ITenantContext tenantContext, SecretUsageScanner scanner)
{
    private readonly Lock _lock = new();
    private Task<SecretUsageIndex>? _usageIndex;

    /// <summary>
    ///     The tenant of the request.
    /// </summary>
    internal ITenantContext TenantContext { get; } = tenantContext;

    /// <summary>
    ///     The secret usage index of the tenant, scanned at most once per request.
    /// </summary>
    internal Task<SecretUsageIndex> GetUsageIndexAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return _usageIndex ??= scanner.ScanAsync(TenantContext, cancellationToken);
        }
    }
}
