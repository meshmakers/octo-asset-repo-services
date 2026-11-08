// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;

/// <summary>
///     How the stream-data read role requirement is applied (AB#8559).
/// </summary>
public enum StreamDataReadRoleEnforcementMode
{
    /// <summary>
    ///     The role is not checked; the behaviour before AB#8559 (only the data-policy check of AB#4973 applies).
    /// </summary>
    Off = 0,

    /// <summary>
    ///     A caller without a stream-data role still reads, but the read is logged as a warning ("would be denied"),
    ///     at most once per caller, tenant and query kind within <see cref="StreamDataReadAuthorizationOptions.WarnLogInterval" />.
    ///     The default of this release: it finds the callers that need a role before switching to <see cref="Enforce" />.
    /// </summary>
    Warn = 1,

    /// <summary>
    ///     A caller without a stream-data role is refused with the GraphQL error code <c>StreamDataReadRoleRequired</c>.
    /// </summary>
    Enforce = 2
}

/// <summary>
///     Options of the stream-data read role requirement (AB#8559). Bound from the <c>"StreamDataReadAuthorization"</c>
///     configuration section, i.e. <c>OCTO_STREAMDATAREADAUTHORIZATION__ROLEENFORCEMENT=Off|Warn|Enforce</c> and
///     <c>OCTO_STREAMDATAREADAUTHORIZATION__WARNLOGINTERVAL=01:00:00</c>.
/// </summary>
/// <remarks>
///     A stream-data read (the transient queries <c>simple</c>, <c>aggregation</c>, <c>groupingAggregation</c>,
///     <c>downsampling</c>, the persisted <c>streamDataQuery</c> and its <c>rows</c>/<c>aggregations</c>) requires one of
///     the tenant roles <c>StreamDataReader</c>, <c>StreamDataWriter</c> or <c>StreamDataAdmin</c>. Before AB#8559 every
///     authenticated token of the tenant read all stream data, because the GraphQL endpoint only requires an
///     authenticated user and the data-policy check is open for types without a policy.
/// </remarks>
public class StreamDataReadAuthorizationOptions
{
    /// <summary>
    ///     The configuration section name.
    /// </summary>
    public const string SectionName = "StreamDataReadAuthorization";

    /// <summary>
    ///     <see cref="StreamDataReadRoleEnforcementMode.Off" />, <see cref="StreamDataReadRoleEnforcementMode.Warn" />
    ///     (default) or <see cref="StreamDataReadRoleEnforcementMode.Enforce" />. Switch an environment to
    ///     <c>Enforce</c> once the warn log (<c>StreamDataReadRoleEnforcement=Enforce (AB#8559)</c>) names no caller
    ///     that still needs a role.
    /// </summary>
    public StreamDataReadRoleEnforcementMode RoleEnforcement { get; set; } = StreamDataReadRoleEnforcementMode.Warn;

    /// <summary>
    ///     In <see cref="StreamDataReadRoleEnforcementMode.Warn" />, the warning for the same caller, tenant and query
    ///     kind is written at most once within this interval. Default one hour; <see cref="TimeSpan.Zero" /> logs every
    ///     read.
    /// </summary>
    public TimeSpan WarnLogInterval { get; set; } = TimeSpan.FromHours(1);
}
