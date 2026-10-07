using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Secrets;

/// <summary>
///     Builds the <see cref="SecretUsageIndex" /> of a tenant: every <c>RevealSecret@1</c> node of every
///     pipeline definition (handover §7, Q5). A full scan per call - callers cache the index per request.
/// </summary>
internal sealed class SecretUsageScanner(IPipelineDefinitionSource source, ILogger<SecretUsageScanner> logger)
{
    /// <summary>
    ///     Scans the tenant's pipelines.
    /// </summary>
    internal async Task<SecretUsageIndex> ScanAsync(ITenantContext tenantContext, CancellationToken cancellationToken)
    {
        var pipelines = await source.GetPipelinesAsync(tenantContext, cancellationToken);
        if (pipelines.Count == 0)
        {
            return SecretUsageIndex.Empty;
        }

        var references = new List<RevealSecretReference>();
        foreach (var pipeline in pipelines)
        {
            references.AddRange(RevealSecretNodeParser.Parse(pipeline, logger));
        }

        logger.LogDebug("Tenant {TenantId}: {NodeCount} RevealSecret@1 node(s) in {PipelineCount} pipeline(s)",
            tenantContext.TenantId, references.Count, pipelines.Count);
        return new SecretUsageIndex(references);
    }
}
