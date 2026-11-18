using System.Security.Claims;
using System.Text.Json;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;

/// <summary>
///     The REST lifecycle endpoints of <c>StreamDataController</c> and the deprecated <c>streamData</c> mutations call the
///     Archive methods of System.StreamData through this facade: one call is one CK method call
///     through <see cref="ICkMethodGateway" /> - caller tenant (<c>METHOD_FORBIDDEN</c> / <c>tenant-not-allowed</c>), declared
///     role (<c>StreamDataAdmin</c>), data permission on the target, parameter validation - and then
///     <see cref="ICkMethodDispatcher" /> on the in-process path: the handler runs in this process, detached from
///     the request's Mongo session, and the invocation pipeline writes exactly one Method audit event.
/// </summary>
/// <remarks>
///     <see cref="IsHosted" /> follows <c>Methods:StreamData:Enabled</c> (default true). While the switch is off no handler is
///     hosted, so the callers keep their direct lifecycle call (a configuration switch never turns a working REST
///     endpoint or mutation off).
/// </remarks>
internal interface IArchiveMethodFacade
{
    /// <summary><c>true</c> when the archive method handlers are hosted (<c>Methods:StreamData:Enabled</c>).</summary>
    bool IsHosted { get; }

    /// <summary>
    ///     Calls <paramref name="methodId" /> on the archive <paramref name="targetRtId" /> of the tenant
    ///     <paramref name="tenant" /> as the caller <paramref name="user" />. Never throws for a refused or failing call; the
    ///     failure is the returned envelope.
    /// </summary>
    /// <param name="tenant">The tenant of the request.</param>
    /// <param name="ckTypeId">The declaring CK type of the method (<see cref="ArchiveMethodFacade.ArchiveCkTypeId" /> or <see cref="ArchiveMethodFacade.RollupArchiveCkTypeId" />).</param>
    /// <param name="methodId">Qualified method id (<c>SystemStreamDataCkIds.…MethodId</c>).</param>
    /// <param name="targetRtId">Runtime id of the archive.</param>
    /// <param name="parameters">The generated parameter object of the method.</param>
    /// <param name="user">The authenticated caller of the request.</param>
    /// <param name="accessToken">The bearer token of the request, forwarded like the GraphQL method fields do. Sensitive.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    Task<CkMethodResponse> InvokeAsync<TParameters>(ITenantContext tenant, string ckTypeId, string methodId,
        OctoObjectId targetRtId, TParameters parameters, ClaimsPrincipal user, string? accessToken,
        CancellationToken cancellationToken) where TParameters : class;
}

/// <inheritdoc />
internal sealed class ArchiveMethodFacade(
    ICkMethodGateway gateway,
    IRtDataPermissionEvaluator dataPermissions,
    IOptions<StreamDataMethodsOptions> options) : IArchiveMethodFacade
{
    /// <summary>CK type id of the abstract archive (the declaring type of the lifecycle methods).</summary>
    public const string ArchiveCkTypeId = "System.StreamData/Archive";

    /// <summary>CK type id of the rollup archive (the declaring type of Freeze, Unfreeze and RewindWatermark).</summary>
    public const string RollupArchiveCkTypeId = "System.StreamData/RollupArchive";

    /// <inheritdoc />
    public bool IsHosted => options.Value.Enabled;

    /// <inheritdoc />
    public Task<CkMethodResponse> InvokeAsync<TParameters>(ITenantContext tenant, string ckTypeId, string methodId,
        OctoObjectId targetRtId, TParameters parameters, ClaimsPrincipal user, string? accessToken,
        CancellationToken cancellationToken) where TParameters : class
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(user);

        var call = new CkMethodGatewayCall(tenant.TenantId, ckTypeId, methodId, targetRtId.ToString(),
            JsonSerializer.Serialize(parameters, CkMethodJson.Options), user, accessToken);

        // The target is loaded only after the gateway authorized the caller, through a session of its own (never the
        // request's transaction), with the caller's rights - the same check as the GraphQL method fields.
        var targetAccess = CkMethodTargetAccessChecker.ForInstance(tenant.GetTenantRepository(),
            Helpers.GetSecurityContext(user), dataPermissions, new RtCkId<CkTypeId>(ckTypeId), targetRtId);
        return gateway.InvokeAsync(call, targetAccess, cancellationToken);
    }

    /// <summary>
    ///     The transition record of a successful lifecycle or rollup call (the result of every method but
    ///     <c>DeleteArchive-1</c>), read with the serializer the typed handler wrote it with. <c>null</c> when the envelope
    ///     carries none.
    /// </summary>
    public static RtArchiveTransitionResultRecord? TransitionOf(CkMethodResponse response)
    {
        if (!response.Success || string.IsNullOrEmpty(response.ResultJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(response.ResultJson);
        var root = document.RootElement;
        // the typed result wraps its value; accept the bare record as well
        var record = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var value) ? value : root;
        return record.Deserialize<RtArchiveTransitionResultRecord>(CkMethodJson.Options);
    }
}
