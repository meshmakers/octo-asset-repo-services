using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Secrets;

/// <summary>
///     Reads the pipeline definitions of a tenant for the secret usage scan (handover §7, Q5).
/// </summary>
internal interface IPipelineDefinitionSource
{
    /// <summary>
    ///     Returns every pipeline of the tenant with its definition and data flow. Empty when the tenant has no
    ///     communication model.
    /// </summary>
    Task<IReadOnlyList<PipelineDefinitionInfo>> GetPipelinesAsync(ITenantContext tenantContext,
        CancellationToken cancellationToken);
}

/// <summary>
///     <see cref="IPipelineDefinitionSource" /> over the tenant database: <c>System.Communication/Pipeline</c>
///     entities (attribute <c>PipelineDefinition</c>) and their parent <c>System.Communication/DataFlow</c>
///     (association <c>System/ParentChild</c>, pipeline = child).
/// </summary>
internal sealed class TenantPipelineDefinitionSource(ICkCacheService ckCacheService) : IPipelineDefinitionSource
{
    internal const string PipelineCkTypeId = "System.Communication/Pipeline";
    internal const string DataFlowCkTypeId = "System.Communication/DataFlow";
    internal const string ParentChildRoleId = "System/ParentChild";
    private const string PipelineDefinitionAttribute = "PipelineDefinition";
    private const string NameAttribute = "Name";
    private const int PageSize = 200;

    /// <inheritdoc />
    public async Task<IReadOnlyList<PipelineDefinitionInfo>> GetPipelinesAsync(ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var pipelineTypeId = new RtCkId<CkTypeId>(PipelineCkTypeId);
        if (!ckCacheService.TryGetRtCkType(tenantContext.TenantId, pipelineTypeId, out _))
        {
            return [];
        }

        var repository = tenantContext.GetTenantRepository();
        var session = repository.GetSession();

        var pipelines = new List<RtEntity>();
        var skip = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await repository.GetRtEntitiesByTypeAsync(session, pipelineTypeId,
                RtEntityQueryOptions.Create(), skip, PageSize);
            pipelines.AddRange(page.Items);
            skip += PageSize;
            if (page.Items.Count() < PageSize || skip >= page.TotalCount)
            {
                break;
            }
        }

        if (pipelines.Count == 0)
        {
            return [];
        }

        var dataFlowByPipeline = await GetDataFlowsAsync(tenantContext, repository, session, pipelines);

        return pipelines.Select(pipeline =>
        {
            dataFlowByPipeline.TryGetValue(pipeline.RtId, out var dataFlow);
            return new PipelineDefinitionInfo(pipeline.RtId,
                pipeline.GetAttributeValueOrDefault(NameAttribute) as string,
                dataFlow?.RtId,
                dataFlow?.GetAttributeValueOrDefault(NameAttribute) as string,
                pipeline.GetAttributeValueOrDefault(PipelineDefinitionAttribute) as string);
        }).ToList();
    }

    private async Task<Dictionary<OctoObjectId, RtEntity>> GetDataFlowsAsync(ITenantContext tenantContext,
        ITenantRepository repository, IOctoSession session, IReadOnlyCollection<RtEntity> pipelines)
    {
        var result = new Dictionary<OctoObjectId, RtEntity>();
        var dataFlowTypeId = new RtCkId<CkTypeId>(DataFlowCkTypeId);
        if (!ckCacheService.TryGetRtCkType(tenantContext.TenantId, dataFlowTypeId, out _))
        {
            return result;
        }

        var associations = await repository.GetRtAssociationsAsync(session,
            pipelines.Select(p => p.ToRtEntityId()).ToList(),
            RtAssociationExtendedQueryOptions.Create(GraphDirections.Any,
                new RtCkId<CkAssociationRoleId>(ParentChildRoleId)));

        var dataFlowIdByPipeline = new Dictionary<OctoObjectId, OctoObjectId>();
        foreach (var (origin, resultSet) in associations)
        {
            foreach (var association in resultSet.Items)
            {
                if (association.OriginRtId == origin.RtId && association.TargetCkTypeId == dataFlowTypeId)
                {
                    dataFlowIdByPipeline.TryAdd(origin.RtId, association.TargetRtId);
                }
                else if (association.TargetRtId == origin.RtId && association.OriginCkTypeId == dataFlowTypeId)
                {
                    dataFlowIdByPipeline.TryAdd(origin.RtId, association.OriginRtId);
                }
            }
        }

        if (dataFlowIdByPipeline.Count == 0)
        {
            return result;
        }

        var dataFlows = await repository.GetRtEntitiesByIdAsync(session, dataFlowTypeId,
            dataFlowIdByPipeline.Values.Distinct().ToList(), RtEntityQueryOptions.Create());
        var dataFlowById = dataFlows.Items.ToDictionary(d => d.RtId);
        foreach (var (pipelineRtId, dataFlowRtId) in dataFlowIdByPipeline)
        {
            if (dataFlowById.TryGetValue(dataFlowRtId, out var dataFlow))
            {
                result[pipelineRtId] = dataFlow;
            }
        }

        return result;
    }
}
