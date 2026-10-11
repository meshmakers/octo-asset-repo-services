namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.Migration;

/// <summary>
///     Options of the move of System.Reporting file data to System.Files (AB#6175, configuration section
///     <c>FilesMigration</c>, environment variables <c>OCTO_FilesMigration__…</c>).
/// </summary>
public class FilesMigrationOptions
{
    /// <summary>
    ///     Name of the configuration section.
    /// </summary>
    public const string SectionName = "FilesMigration";

    /// <summary>
    ///     Global switch of the sweep (tenant start and straggler timer); off by default. Since AB#6473 it is only
    ///     the first level: a tenant is swept only when it is also listed in <see cref="SweepTenants" />. It is switched on per cluster in
    ///     the rollout (AB#6183) once Reporting (AB#6176) and the consumers (AB#6181) read System.Files, so a
    ///     deploy of this service alone never moves data (risk R5). The pre-check endpoint works either way.
    /// </summary>
    public bool SweepEnabled { get; set; }

    /// <summary>
    ///     Tenant ids the sweep is allowed to run for (AB#6473; <c>OCTO_FilesMigration__SweepTenants__0</c>,
    ///     <c>__1</c>, …). Evaluated on top of <see cref="SweepEnabled" />: a tenant is swept only when the
    ///     global switch is on AND the tenant is listed. Empty (default) sweeps nothing, so switching
    ///     <see cref="SweepEnabled" /> on never moves data of a tenant the rollout operator did not name.
    ///     Matching is exact and case-insensitive.
    /// </summary>
    public string[] SweepTenants { get; set; } = [];

    /// <summary>
    ///     True when the tenant is in <see cref="SweepTenants" /> (exact, case-insensitive).
    /// </summary>
    public bool IsTenantListed(string tenantId)
    {
        return SweepTenants.Any(t => string.Equals(t?.Trim(), tenantId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     True when the sweep may run for the tenant: the global switch is on and the tenant is listed.
    /// </summary>
    public bool IsSweepAllowed(string tenantId) => SweepEnabled && IsTenantListed(tenantId);

    /// <summary>
    ///     Interval of the straggler sweep for tenants whose last sweep found legacy data (default 10 min, Q3).
    /// </summary>
    public TimeSpan StragglerSweepInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     Entities moved per transaction.
    /// </summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    ///     Largest number of literal references the pre-check lists (the count is always complete).
    /// </summary>
    public int MaxReportedReferences { get; set; } = 1000;

    /// <summary>
    ///     Largest number of orphan ids the pre-check lists (the count is always complete).
    /// </summary>
    public int MaxReportedOrphans { get; set; } = 100;

    /// <summary>
    ///     How long a tenant stays on the straggler timer after the last sweep that found legacy data
    ///     (default 24 h). Ends earlier once System.Reporting 3.0.0 or later is installed in the tenant.
    /// </summary>
    public TimeSpan StragglerWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    ///     Lifetime of the per-tenant sweep lease (renewed after every batch). A pod that dies holding it
    ///     blocks the tenant's sweep for at most this long.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Upper bound of one literal reference scan (all collections). A scan that runs longer is cut off
    ///     and reported as incomplete.
    /// </summary>
    public TimeSpan ScanTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Minimum interval between two warnings for the same persistent condition of a tenant
    ///     (System.Files missing, root name conflict); repeats in between are logged at debug level.
    /// </summary>
    public TimeSpan RepeatedWarningInterval { get; set; } = TimeSpan.FromHours(1);
}
