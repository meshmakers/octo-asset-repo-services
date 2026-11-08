using System.Collections.Concurrent;
using System.Security.Claims;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

/// <summary>Outcome of <see cref="StreamDataReadRoleGate.Check" />.</summary>
internal enum StreamDataReadRoleDecision
{
    /// <summary>The caller holds a stream-data role, is a system context, or the mode is <c>Off</c>.</summary>
    Allowed,

    /// <summary>The caller holds no stream-data role and the mode is <c>Warn</c>: the read goes ahead, logged.</summary>
    AllowedByWarn,

    /// <summary>The caller holds no stream-data role and the mode is <c>Enforce</c>.</summary>
    Denied
}

/// <summary>
///     Thrown when a stream-data read is refused because the caller holds none of the stream-data roles (AB#8559).
///     Mapped to the GraphQL error code <see cref="AssetRepositoryServiceConstants.StreamDataReadRoleRequiredCode" />.
/// </summary>
internal sealed class StreamDataReadRoleRequiredException() : Exception(
    "Access is denied: reading stream data requires one of the roles " +
    $"'{CommonConstants.StreamDataReaderRole}', '{CommonConstants.StreamDataWriterRole}' or " +
    $"'{CommonConstants.StreamDataAdminRole}' in the tenant. The token carries no such role; ask a tenant " +
    "administrator to grant it.");

/// <summary>
///     The stream-data read role requirement (AB#8559): a stream-data read needs one of the tenant roles
///     <c>StreamDataReader</c>, <c>StreamDataWriter</c> or <c>StreamDataAdmin</c>, staged by
///     <see cref="StreamDataReadAuthorizationOptions.RoleEnforcement" /> (<c>Off</c> / <c>Warn</c> / <c>Enforce</c>).
/// </summary>
/// <remarks>
///     <para>
///         System contexts always read: an <see cref="RtSecurityContext.IsSystem" /> context and an in-process execution
///         without any principal (<c>null</c> user — the HTTP endpoint always has a principal and requires an
///         authenticated one). Pipelines of the mesh adapter read the archives in-process through the engine and never
///         reach this gate.
///     </para>
///     <para>
///         Roles are probed under both claim spellings (<see cref="TenantRoleAuthorizationHandler.HasTenantRole" />,
///         AB#7319).
///     </para>
/// </remarks>
internal sealed class StreamDataReadRoleGate
{
    private const int MaxWarnEntries = 10_000;

    private static readonly string[] StreamDataRoles =
    [
        CommonConstants.StreamDataReaderRole,
        CommonConstants.StreamDataWriterRole,
        CommonConstants.StreamDataAdminRole
    ];

    private readonly IOptionsMonitor<StreamDataReadAuthorizationOptions> _options;
    private readonly ILogger<StreamDataReadRoleGate> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastWarned = new(StringComparer.Ordinal);

    public StreamDataReadRoleGate(IOptionsMonitor<StreamDataReadAuthorizationOptions> options,
        ILogger<StreamDataReadRoleGate> logger, TimeProvider? timeProvider = null)
    {
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    ///     Fallback for service collections that do not register the gate (unit-test hosts): the default options
    ///     (<c>Warn</c>), no logging.
    /// </summary>
    internal static StreamDataReadRoleGate Default { get; } = new(
        new StaticOptionsMonitor(new StreamDataReadAuthorizationOptions()),
        NullLogger<StreamDataReadRoleGate>.Instance);

    /// <summary>
    ///     Throws <see cref="StreamDataReadRoleRequiredException" /> when the read is refused.
    /// </summary>
    internal void EnsureAllowed(ClaimsPrincipal? user, RtSecurityContext securityContext, string tenantId,
        string queryKind)
    {
        if (Check(user, securityContext, tenantId, queryKind) == StreamDataReadRoleDecision.Denied)
        {
            throw new StreamDataReadRoleRequiredException();
        }
    }

    /// <summary>
    ///     Decides a stream-data read and writes the warn / deny log line.
    /// </summary>
    internal StreamDataReadRoleDecision Check(ClaimsPrincipal? user, RtSecurityContext securityContext,
        string tenantId, string queryKind)
    {
        var options = _options.CurrentValue;
        if (options.RoleEnforcement == StreamDataReadRoleEnforcementMode.Off)
        {
            return StreamDataReadRoleDecision.Allowed;
        }

        if (securityContext.IsSystem || user == null)
        {
            return StreamDataReadRoleDecision.Allowed;
        }

        if (StreamDataRoles.Any(role => TenantRoleAuthorizationHandler.HasTenantRole(user, role)))
        {
            return StreamDataReadRoleDecision.Allowed;
        }

        // Ids only: never a token, a name or an e-mail address.
        var subject = user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var clientId = user.FindFirst("client_id")?.Value;

        if (options.RoleEnforcement == StreamDataReadRoleEnforcementMode.Warn)
        {
            if (ShouldWarn(tenantId, subject, clientId, queryKind, options.WarnLogInterval))
            {
                // This line is the inventory the switch to Enforce is decided on.
                _logger.LogWarning(
                    "Stream-data read without a stream-data role allowed by StreamDataReadRoleEnforcement=Warn: query {QueryKind} in tenant {TenantId} by subject {Subject} / client {ClientId}, required any of [{RequiredRoles}]. " +
                    "This would be denied with StreamDataReadRoleEnforcement=Enforce (AB#8559)",
                    queryKind, tenantId, subject, clientId, string.Join(",", StreamDataRoles));
            }

            return StreamDataReadRoleDecision.AllowedByWarn;
        }

        _logger.LogWarning(
            "Stream-data read denied, stream-data role missing: query {QueryKind} in tenant {TenantId} by subject {Subject} / client {ClientId}, required any of [{RequiredRoles}] (AB#8559)",
            queryKind, tenantId, subject, clientId, string.Join(",", StreamDataRoles));
        return StreamDataReadRoleDecision.Denied;
    }

    private bool ShouldWarn(string tenantId, string? subject, string? clientId, string queryKind, TimeSpan interval)
    {
        var now = _timeProvider.GetUtcNow();
        var key = string.Join('\u001f', tenantId, subject ?? string.Empty, clientId ?? string.Empty, queryKind);

        if (_lastWarned.TryGetValue(key, out var last) && now - last < interval)
        {
            return false;
        }

        if (_lastWarned.Count >= MaxWarnEntries)
        {
            // Bounded memory: forgetting the window only means one extra warning per caller.
            _lastWarned.Clear();
        }

        _lastWarned[key] = now;
        return true;
    }

    private sealed class StaticOptionsMonitor(StreamDataReadAuthorizationOptions value)
        : IOptionsMonitor<StreamDataReadAuthorizationOptions>
    {
        public StreamDataReadAuthorizationOptions CurrentValue => value;

        public StreamDataReadAuthorizationOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<StreamDataReadAuthorizationOptions, string?> listener) => null;
    }
}
