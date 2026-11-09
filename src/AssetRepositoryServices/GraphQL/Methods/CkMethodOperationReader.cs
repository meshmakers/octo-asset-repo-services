using System.Security.Claims;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;

/// <summary>Filter of <see cref="ICkMethodOperationReader.ListAsync" /> as the client sent it.</summary>
/// <param name="MethodId">Only operations of this qualified, version-less method id (exact match).</param>
/// <param name="State">Only operations in this state.</param>
/// <param name="SinceUtc">Only operations accepted at or after this time (UTC).</param>
/// <param name="AllOwners"><c>true</c>: every caller's operations of the tenant (tenant administrators only).</param>
/// <param name="Skip">Operations to skip (0 or more).</param>
/// <param name="Take">Page size (1 to <see cref="CkMethodOperationQuery.MaxTake" />).</param>
internal sealed record CkMethodOperationListFilter(
    string? MethodId,
    CkMethodOperationState? State,
    DateTime? SinceUtc,
    bool AllOwners,
    int Skip,
    int Take);

/// <summary>
///     AB#8444 (F3.6, OD-5 point 5): reads operation records for <c>runtime.operations</c> through the store contract of
///     AB#8443 and applies who may read them. Within the tenant of the route only: the caller who started the operation
///     (same <c>sub</c>, or the same <c>client_id</c> for a client-credentials caller without <c>sub</c>) and tenant
///     administrators (<c>Methods:Authorization:AdministratorRoles</c>, default <c>AdminPanelManagement</c>, the same
///     setting the method authorization uses). An unknown, expired, foreign (another caller's, another tenant's) or
///     malformed id is the same <see cref="CkMethodOperationQueryException.NotFound" />: there is no existence oracle.
/// </summary>
internal interface ICkMethodOperationReader
{
    /// <summary>The operation <paramref name="operationId" /> of the tenant, if the caller may read it.</summary>
    /// <exception cref="CkMethodOperationQueryException"><c>OPERATION_NOT_FOUND</c>.</exception>
    Task<CkMethodOperationRecord> GetAsync(string tenantId, ClaimsPrincipal user, string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     A page of the tenant's operations the caller may list: its own, or with <see cref="CkMethodOperationListFilter.AllOwners" />
    ///     every caller's (tenant administrators only).
    /// </summary>
    /// <exception cref="CkMethodOperationQueryException"><c>OPERATION_QUERY_REFUSED</c>.</exception>
    Task<CkMethodOperationPage> ListAsync(string tenantId, ClaimsPrincipal user, CkMethodOperationListFilter filter,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
internal sealed class CkMethodOperationReader(
    ICkMethodOperationStore store,
    IOptions<CkMethodAuthorizationOptions> authorizationOptions) : ICkMethodOperationReader
{
    /// <summary>Longest operation id that is looked up; a longer one cannot exist and is not found.</summary>
    internal const int MaxOperationIdLength = 128;

    /// <inheritdoc />
    public async Task<CkMethodOperationRecord> GetAsync(string tenantId, ClaimsPrincipal user, string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(user);

        var id = operationId?.Trim();
        if (string.IsNullOrEmpty(id) || id.Length > MaxOperationIdLength)
        {
            throw CkMethodOperationQueryException.NotFound();
        }

        var caller = Caller(user);
        if (caller == null)
        {
            throw CkMethodOperationQueryException.NotFound();
        }

        var record = await store.GetAsync(CkMethodOperationTenant.Normalize(tenantId), id, cancellationToken)
            .ConfigureAwait(false);
        if (record == null || !(IsAdministrator(caller.Value.Principal) || IsOwner(record, caller.Value.Owner)))
        {
            throw CkMethodOperationQueryException.NotFound();
        }

        return record;
    }

    /// <inheritdoc />
    public async Task<CkMethodOperationPage> ListAsync(string tenantId, ClaimsPrincipal user,
        CkMethodOperationListFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(filter);

        if (filter.Skip < 0)
        {
            throw CkMethodOperationQueryException.Refused(CkMethodOperationQueryException.ReasonInvalidPaging,
                ("argument", Statics.SkipArg));
        }

        if (filter.Take is < 1 or > CkMethodOperationQuery.MaxTake)
        {
            throw CkMethodOperationQueryException.Refused(CkMethodOperationQueryException.ReasonInvalidPaging,
                ("argument", Statics.TakeArg), ("maxTake", CkMethodOperationQuery.MaxTake.ToString()));
        }

        var caller = Caller(user);
        if (filter.AllOwners)
        {
            if (caller == null || !IsAdministrator(caller.Value.Principal))
            {
                throw CkMethodOperationQueryException.Refused(
                    CkMethodOperationQueryException.ReasonAdministratorsOnly, ("argument", "allOwners"));
            }
        }
        else if (caller == null || caller.Value.Owner.IsNone)
        {
            // no identity to match: nobody's own operations
            return new CkMethodOperationPage([], 0);
        }

        var query = new CkMethodOperationQuery
        {
            TenantId = CkMethodOperationTenant.Normalize(tenantId),
            Owner = filter.AllOwners ? null : caller!.Value.Owner,
            MethodId = string.IsNullOrWhiteSpace(filter.MethodId) ? null : filter.MethodId.Trim(),
            State = filter.State,
            SinceUtc = filter.SinceUtc,
            Skip = filter.Skip,
            Take = filter.Take
        };
        return await store.ListAsync(query, cancellationToken).ConfigureAwait(false);
    }

    private static (CkMethodPrincipal Principal, CkMethodOperationOwner Owner)? Caller(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        // the same mapping the gateway uses for the principal of the call, so the owner matches the record
        var principal = CkMethodPrincipal.FromClaimsPrincipal(user);
        return (principal, CkMethodOperationOwner.Of(principal));
    }

    private static bool IsOwner(CkMethodOperationRecord record, CkMethodOperationOwner caller) =>
        !caller.IsNone && !record.Owner.IsNone && record.Owner == caller;

    private bool IsAdministrator(CkMethodPrincipal principal)
    {
        var configured = authorizationOptions.Value.AdministratorRoles;
        IEnumerable<string> roles = configured.Count > 0 ? configured : CkMethodAuthorizationOptions.DefaultAdministratorRoles;
        return roles.Any(r => !string.IsNullOrWhiteSpace(r) &&
                              principal.Roles.Contains(r, StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>
///     AB#8444: a refused <c>runtime.operations</c> request. Mapped to ONE GraphQL error with <c>extensions.code</c> and
///     <c>extensions.parameters</c> (the shape of the method errors); the message is fixed and carries no value.
/// </summary>
internal sealed class CkMethodOperationQueryException : Exception
{
    /// <summary>Unknown, expired, foreign or malformed operation id (all alike).</summary>
    public const string OperationNotFound = "OPERATION_NOT_FOUND";

    /// <summary>The list request is not allowed or malformed; parameter <c>reason</c>.</summary>
    public const string OperationQueryRefused = "OPERATION_QUERY_REFUSED";

    /// <summary>Reason: <c>allOwners</c> without a tenant administrator role.</summary>
    public const string ReasonAdministratorsOnly = "administrators-only";

    /// <summary>Reason: <c>skip</c> negative or <c>take</c> outside 1 to 200.</summary>
    public const string ReasonInvalidPaging = "invalid-paging";

    private CkMethodOperationQueryException(string code, IReadOnlyDictionary<string, string> parameters,
        string message) : base(message)
    {
        Code = code;
        Parameters = parameters;
    }

    /// <summary>The stable error code.</summary>
    public string Code { get; }

    /// <summary>Language-neutral parameters.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>
    ///     <c>OPERATION_NOT_FOUND</c> without parameters: byte-identical for an unknown, expired, foreign and malformed id.
    /// </summary>
    public static CkMethodOperationQueryException NotFound() =>
        new(OperationNotFound, new Dictionary<string, string>(),
            "The operation was not found. It may never have existed, may have expired, or may not be readable for the caller.");

    /// <summary><c>OPERATION_QUERY_REFUSED</c> with the reason and the offending argument.</summary>
    public static CkMethodOperationQueryException Refused(string reason, params (string Name, string Value)[] parameters)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal) { ["reason"] = reason };
        foreach (var (name, value) in parameters)
        {
            all[name] = value;
        }

        return new CkMethodOperationQueryException(OperationQueryRefused, all,
            $"The operations query was refused: {reason}.");
    }
}
