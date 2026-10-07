using System.Collections.Concurrent;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Secrets;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     AB#5544: the production <see cref="TenantPipelineDefinitionSource" /> plus pipelines added by a test. The test
///     tenant has no System.Communication model, so <c>RevealSecret@1</c> pipelines cannot be created as entities;
///     tests add their definitions here and everything from parsing to the GraphQL response runs unchanged.
/// </summary>
internal sealed class TestPipelineDefinitionSource(ICkCacheService ckCacheService) : IPipelineDefinitionSource
{
    private readonly ConcurrentDictionary<OctoObjectId, PipelineDefinitionInfo> _added = new();

    /// <summary>
    ///     The production source this one extends.
    /// </summary>
    internal TenantPipelineDefinitionSource Inner { get; } = new(ckCacheService);

    internal void Add(PipelineDefinitionInfo pipeline)
    {
        _added[pipeline.PipelineRtId] = pipeline;
    }

    internal void Remove(OctoObjectId pipelineRtId)
    {
        _added.TryRemove(pipelineRtId, out _);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PipelineDefinitionInfo>> GetPipelinesAsync(ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var stored = await Inner.GetPipelinesAsync(tenantContext, cancellationToken);
        return stored.Concat(_added.Values).ToList();
    }
}
