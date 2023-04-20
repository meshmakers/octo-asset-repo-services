using System.Collections.Generic;
using System.Threading.Tasks;
using GraphQL.Server.Transports.AspNetCore;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Microsoft.AspNetCore.Http;

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
    public async ValueTask<IDictionary<string, object>> BuildUserContextAsync(HttpContext httpContext, object payload)
    {
        var tenantId = httpContext.GetTenantId();

        using var systemSession = await _octoService.SystemContext.StartSystemSessionAsync();
        systemSession.StartTransaction();

        var userContext = new GraphQLUserContext
        {
            User = httpContext.User
        };

        if (!string.IsNullOrWhiteSpace(tenantId) &&
            await _octoService.SystemContext.IsTenantExistingAsync(systemSession, tenantId))
        {
            userContext.TenantContext = await _octoService.SystemContext.CreateOrGetTenantContextAsync(tenantId);
        }

        await systemSession.CommitTransactionAsync();
        return userContext;
    }
}
