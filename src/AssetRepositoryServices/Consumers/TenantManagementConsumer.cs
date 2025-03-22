using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Consumers;

/// <summary>
///    Updates jobs for a tenant
/// </summary>
internal class TenantManagementConsumer : IDistributedConsumer<PosUpdateTenant>,
    IDistributedConsumer<PreDeleteTenant>
{
    private readonly ISchemaContext _schemaContext;

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="schemaContext"></param>
    public TenantManagementConsumer(ISchemaContext schemaContext)
    {
        _schemaContext = schemaContext;
    }

    public Task ConsumeAsync(IDistributedContext<PosUpdateTenant> context)
    {
        _schemaContext.Invalidate(context.Message.TenantId);

        return Task.CompletedTask;
    }

    public Task ConsumeAsync(IDistributedContext<PreDeleteTenant> context)
    {
        _schemaContext.Invalidate(context.Message.TenantId);
        return Task.CompletedTask;
    }
}