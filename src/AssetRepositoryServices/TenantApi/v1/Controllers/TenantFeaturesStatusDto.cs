namespace Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;

/// <summary>
///     Aggregate enabled-state of the four per-tenant capabilities the delete/detach guard evaluates
///     (AB#4255): Stream Data, Communication, Reporting, AI Services. Served by
///     <c>GET {tenantId}/v1/features/status</c> for the Refinery Studio Tenant Features panel
///     (AB#4884) — one state source, so panel and guard never disagree.
/// </summary>
/// <remarks>
///     Whether a capability's service is part of the installation at all is deliberately NOT in this
///     DTO (except for Stream Data, whose instance-level kill switch lives in this service):
///     availability comes from the <c>_configuration</c> discovery document, where an empty service
///     URL means "not installed". Replaces the former <c>streamdata/status</c> endpoint.
/// </remarks>
public class TenantFeaturesStatusDto
{
    /// <summary>Stream Data state (tenant flag is engine-owned; instance flag from this service).</summary>
    public required StreamDataFeatureStatusDto StreamData { get; init; }

    /// <summary>Communication state.</summary>
    public required TenantFeatureStatusDto Communication { get; init; }

    /// <summary>Reporting state.</summary>
    public required TenantFeatureStatusDto Reporting { get; init; }

    /// <summary>AI Services state.</summary>
    public required TenantFeatureStatusDto AiServices { get; init; }
}

/// <summary>Enabled-state of one capability for the tenant.</summary>
public class TenantFeatureStatusDto
{
    /// <summary>True when the tenant's capability flag exists and reads enabled.</summary>
    public required bool TenantEnabled { get; init; }
}

/// <summary>
///     Stream Data carries an additional instance-level flag: the deployment-wide
///     <c>StreamData:Enabled</c> kill switch. <see cref="TenantEnabled" /> reports the tenant flag
///     regardless, so a tenant left enabled on an installation without stream data is visible as
///     exactly that (it still blocks tenant delete/detach).
/// </summary>
public class StreamDataFeatureStatusDto
{
    /// <summary>True when stream data is enabled at the instance level (<c>StreamData:Enabled</c>).</summary>
    public required bool InstanceEnabled { get; init; }

    /// <summary>True when the tenant's stream data flag reads enabled.</summary>
    public required bool TenantEnabled { get; init; }
}
