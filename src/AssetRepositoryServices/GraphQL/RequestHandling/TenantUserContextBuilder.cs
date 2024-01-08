using GraphQL.Server.Transports.AspNetCore;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Common;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;

/// <inheritdoc />
// ReSharper disable once ClassNeverInstantiated.Global
public class TenantUserContextBuilder : IUserContextBuilder
{
    private readonly IOctoService _octoService;

    /// <summary>
    ///     c'tor
    /// </summary>
    /// <param name="octoService">Root object for octo services</param>
    public TenantUserContextBuilder(IOctoService octoService)
    {
        _octoService = octoService;
    }

    /// <inheritdoc />
    public async ValueTask<IDictionary<string, object?>?> BuildUserContextAsync(HttpContext httpContext, object? payload)
    {
        var tenantId = httpContext.GetTenantId();

        using var systemSession = await _octoService.SystemContext.GetSystemSessionAsync();
        systemSession.StartTransaction();

        ITenantContext tenantContext = _octoService.SystemContext;
        if (tenantId != null && tenantId.NormalizeString() != _octoService.SystemContext.TenantId)
        {
            tenantContext = await _octoService.SystemContext.GetChildTenantContextAsync(tenantId);
        }

        var userContext = new GraphQlUserContext(httpContext.User, tenantContext);

        await systemSession.CommitTransactionAsync();
        return userContext;
    }
}