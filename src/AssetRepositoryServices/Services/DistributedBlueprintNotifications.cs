using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services;

/// <summary>
///     Bridges engine-level <see cref="IBlueprintNotifications"/> calls onto the
///     distribution event hub. Replaces the engine's default logging-only
///     implementation when this service hosts the blueprint API.
/// </summary>
/// <remarks>
///     Engine record types are mapped to the cross-service event records in
///     <c>Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages</c>.
///     Engine types (BlueprintId, enums, lists of BlueprintId) become strings
///     for wire-level stability and easier subscriber tooling.
/// </remarks>
internal sealed class DistributedBlueprintNotifications : IBlueprintNotifications
{
    private readonly IDistributionEventHubService _eventHub;
    private readonly ILogger<DistributedBlueprintNotifications> _logger;

    public DistributedBlueprintNotifications(
        IDistributionEventHubService eventHub,
        ILogger<DistributedBlueprintNotifications> logger)
    {
        _eventHub = eventHub;
        _logger = logger;
    }

    public async Task NotifyAppliedAsync(BlueprintAppliedNotification notification, CancellationToken cancellationToken = default)
    {
        var ev = new BlueprintApplied(
            notification.TenantId,
            notification.BlueprintId.FullName,
            notification.ApplicationMode.ToString(),
            notification.EntitiesAdded,
            notification.EntitiesUpdated,
            notification.EntitiesDeleted,
            notification.CorrelationId,
            notification.Timestamp);

        await PublishAsync(ev, "BlueprintApplied", cancellationToken).ConfigureAwait(false);
    }

    public async Task NotifyUpdatedAsync(BlueprintUpdatedNotification notification, CancellationToken cancellationToken = default)
    {
        var ev = new BlueprintUpdated(
            notification.TenantId,
            notification.BlueprintId.FullName,
            notification.FromVersion?.FullName,
            notification.UpdateMode.ToString(),
            notification.EntitiesAdded,
            notification.EntitiesUpdated,
            notification.EntitiesDeleted,
            notification.CorrelationId,
            notification.Timestamp);

        await PublishAsync(ev, "BlueprintUpdated", cancellationToken).ConfigureAwait(false);
    }

    public async Task NotifyUninstalledAsync(BlueprintUninstalledNotification notification, CancellationToken cancellationToken = default)
    {
        var ev = new BlueprintUninstalled(
            notification.TenantId,
            notification.BlueprintId.FullName,
            notification.CascadedDependencies.Select(d => d.FullName).ToList(),
            notification.CorrelationId,
            notification.Timestamp);

        await PublishAsync(ev, "BlueprintUninstalled", cancellationToken).ConfigureAwait(false);
    }

    public async Task NotifyOperationFailedAsync(BlueprintOperationFailedNotification notification, CancellationToken cancellationToken = default)
    {
        var ev = new BlueprintOperationFailed(
            notification.TenantId,
            notification.BlueprintId?.FullName,
            notification.Operation,
            notification.ErrorMessage,
            notification.CorrelationId,
            notification.Timestamp);

        await PublishAsync(ev, "BlueprintOperationFailed", cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishAsync<TEvent>(TEvent ev, string eventName, CancellationToken cancellationToken)
        where TEvent : class
    {
        try
        {
            await _eventHub.PublishAsync(ev, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Audit publishing must never break a successful operation.
            _logger.LogWarning(ex, "Failed to publish {EventName} to distribution event hub", eventName);
        }
    }
}
