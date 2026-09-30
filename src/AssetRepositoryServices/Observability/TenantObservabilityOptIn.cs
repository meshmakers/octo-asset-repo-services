using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Observability;

/// <summary>
///     Reads a tenant's observability opt-in from <c>System/TenantModeConfiguration</c>.
/// </summary>
public interface ITenantObservabilityOptIn
{
    /// <summary>
    ///     True only when the tenant carries a <c>System/TenantModeConfiguration</c> whose
    ///     <c>PublishCkModelObservability</c> is set. Absent entity, absent attribute, unreadable
    ///     tenant and an unimported System model all answer false — observability is opt-in, and
    ///     "we could not tell" must resolve to "not opted in", never to "measure it anyway".
    /// </summary>
    Task<bool> IsCkModelObservabilityEnabledAsync(string tenantId, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class TenantObservabilityOptIn : ITenantObservabilityOptIn
{
    /// <summary>
    ///     Attribute name on <c>System/TenantModeConfiguration</c>, added in <c>System-2.3.0</c>
    ///     (octo-construction-kit-engine <c>a43d17f</c>), optional, default <c>false</c>.
    ///
    ///     Read by name rather than through the generated <c>RtTenantModeConfiguration</c> property
    ///     on purpose. The whole attribute set round-trips through one <c>_attributes</c> dictionary
    ///     (<c>RtAttributeDictionarySerializer</c>), so reading by name works on any tenant whose
    ///     document carries the field — including while this service still compiles against a
    ///     <c>Meshmakers.Octo.ConstructionKit.Models.System</c> package generated from System-2.2.2,
    ///     which is the situation on every lane until that package is rebuilt. Binding to the typed
    ///     property would have made this service unbuildable until then for no gain.
    /// </summary>
    public const string PublishCkModelObservabilityAttribute = "PublishCkModelObservability";

    private readonly ISystemContext _systemContext;

    /// <summary>
    ///     Constructor
    /// </summary>
    public TenantObservabilityOptIn(ISystemContext systemContext)
    {
        _systemContext = systemContext;
    }

    /// <inheritdoc />
    public async Task<bool> IsCkModelObservabilityEnabledAsync(string tenantId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tenantContext = await _systemContext.TryFindTenantContextAsync(tenantId);
        if (tenantContext == null)
        {
            return false;
        }

        var repository = tenantContext.GetTenantRepository();
        var session = repository.GetSession();

        var resultSet = await repository.GetRtEntitiesByTypeAsync(session,
            SystemCkIds.RtCkTenantModeConfigurationTypeId,
            RtEntityQueryOptions.Create(), take: 1);

        var configuration = resultSet.Items.FirstOrDefault();
        return configuration != null &&
               configuration.GetAttributeValueOrStandard(PublishCkModelObservabilityAttribute, false);
    }
}
