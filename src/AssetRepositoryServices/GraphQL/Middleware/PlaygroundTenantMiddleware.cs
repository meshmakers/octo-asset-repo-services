using System.Diagnostics.CodeAnalysis;
using System.Text;
using GraphQL.Server.Ui.Altair;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Middleware;

/// <summary>
///     A middleware for Playground
/// </summary>
public class PlaygroundTenantMiddleware
{
    private readonly IOptions<OctoAssetRepositoryServicesOptions> _assetOptions;
    private readonly IOctoService _octoService;
    private readonly AltairOptions _options;

    private string? _lastTenantId;

    /// <summary>
    ///     The page model used to render Playground
    /// </summary>
    private AltairPageModel? _pageModel;

    /// <summary>
    ///     Create a new <see cref="PlaygroundTenantMiddleware" />
    /// </summary>
    /// <param name="next">The next request delegate; not used, this is a terminal middleware.</param>
    /// <param name="assetOptions">Asset options to check the tenantId</param>
    /// <param name="octoService">Octo service instance to check the tenantId</param>
    /// <param name="options">Options to customize middleware</param>
    [SuppressMessage("Style", "IDE0060:Remove unused parameter",
        Justification = "ASP.NET Core conventions")]
    // ReSharper disable once UnusedParameter.Local
    public PlaygroundTenantMiddleware(RequestDelegate next,
        IOptions<OctoAssetRepositoryServicesOptions> assetOptions, IOctoService octoService, AltairOptions options)
    {
        _assetOptions = assetOptions;
        _octoService = octoService;
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }


    /// <summary>
    ///     Try to execute the logic of the middleware
    /// </summary>
    /// <param name="httpContext">The HttpContext</param>
    public async Task Invoke(HttpContext httpContext)
    {
        if (httpContext == null)
        {
            throw new ArgumentNullException(nameof(httpContext));
        }

        var tenantId = httpContext.GetTenantId();
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            httpContext.Response.StatusCode = 400; //BadRequest
            await httpContext.Response.WriteAsync("Missing tenant");
            return;
        }
        
        using var systemSession = await _octoService.SystemContext.GetAdminSessionAsync();
        systemSession.StartTransaction();

        if (tenantId.NormalizeString() != _octoService.SystemContext.TenantId &&
            !await _octoService.SystemContext.IsChildTenantExistingAsync(systemSession, tenantId))
        {
            httpContext.Response.StatusCode = 403; //NotFound
            await httpContext.Response.WriteAsync("Invalid tenant");
            return;
        }

        await systemSession.CommitTransactionAsync();

        await InvokePlayground(httpContext.Response, tenantId);
    }

    private async Task InvokePlayground(HttpResponse httpResponse, string? tenantId)
    {
        
        if (string.Compare(tenantId, _lastTenantId, StringComparison.OrdinalIgnoreCase) != 0)
        {
            _lastTenantId = tenantId;
            _pageModel =
                new AltairPageModel(_assetOptions.Value.PublicUrl.EnsureEndsWith("/"), _options.GraphQLEndPoint.Replace("{tenantId}", tenantId),
                    _options);
        }

        if (_pageModel != null)
        {
            var data = Encoding.UTF8.GetBytes(_pageModel.Render());

            httpResponse.ContentType = "text/html";
            httpResponse.StatusCode = 200;
            await httpResponse.Body.WriteAsync(data, 0, data.Length);
        }
        else
        {
            httpResponse.StatusCode = 400;
        }
    }
}