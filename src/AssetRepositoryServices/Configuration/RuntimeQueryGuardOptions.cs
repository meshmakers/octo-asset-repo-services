// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;

/// <summary>
///     How the unbounded runtime-entity query guard is applied (AB#8898).
/// </summary>
public enum RuntimeQueryGuardMode
{
    /// <summary>
    ///     No check, no log, no metric: the behaviour before AB#8898.
    /// </summary>
    Off = 0,

    /// <summary>
    ///     An unbounded query runs as before; it is logged as a warning and counted in the metric
    ///     <c>octo.asset.runtime_query.unbounded</c>. The default: it shows who sends such queries before they are refused.
    /// </summary>
    Warn = 1,

    /// <summary>
    ///     An unbounded query is refused before any database call with the GraphQL error code <c>ASSET1007</c>; logged and
    ///     counted as in <see cref="Warn" />. The result is never capped silently.
    /// </summary>
    Enforce = 2
}

/// <summary>
///     Options of the unbounded runtime-entity query guard (AB#8898, Feature AB#8793). Bound from the
///     <c>"RuntimeQueryGuard"</c> configuration section, i.e. <c>OCTO_RUNTIMEQUERYGUARD__MODE=Off|Warn|Enforce</c> and
///     <c>OCTO_RUNTIMEQUERYGUARD__MAXPAGESIZE=1000</c>.
/// </summary>
/// <remarks>
///     The guard acts only at the API boundary of the asset repository (the GraphQL entry points
///     <c>runtime.runtimeEntities</c>, <c>runtime.&lt;CkType&gt;</c>, <c>runtime.runtimeQuery.rows</c> and
///     <c>runtime.transientQuery.*.rows</c>; SDK, CLI and MCP reach the asset repository through them). In-process engine
///     calls (<c>ITenantRepository</c>) of services, background jobs, blueprint installs and CK migrations are never
///     checked, refused, logged or counted.
/// </remarks>
public class RuntimeQueryGuardOptions
{
    /// <summary>
    ///     The configuration section name.
    /// </summary>
    public const string SectionName = "RuntimeQueryGuard";

    /// <summary>
    ///     The default of <see cref="MaxPageSize" /> (decided 2026-11-12, AB#8793).
    /// </summary>
    public const int DefaultMaxPageSize = 1000;

    /// <summary>
    ///     <see cref="RuntimeQueryGuardMode.Off" />, <see cref="RuntimeQueryGuardMode.Warn" /> (default) or
    ///     <see cref="RuntimeQueryGuardMode.Enforce" />.
    /// </summary>
    public RuntimeQueryGuardMode Mode { get; set; } = RuntimeQueryGuardMode.Warn;

    /// <summary>
    ///     The largest page a query may request with <c>first</c>, and the largest number of keys (<c>rtId</c> /
    ///     <c>rtIds</c>) a key lookup without <c>first</c> may carry. A query that selects items with no <c>first</c>, or
    ///     with <c>first</c> above this value, is unbounded. Default 1000; values below 1 fall back to the default.
    /// </summary>
    public int MaxPageSize { get; set; } = DefaultMaxPageSize;

    /// <summary>
    ///     <see cref="MaxPageSize" />, or <see cref="DefaultMaxPageSize" /> when it is not positive.
    /// </summary>
    internal int EffectiveMaxPageSize => MaxPageSize > 0 ? MaxPageSize : DefaultMaxPageSize;
}
