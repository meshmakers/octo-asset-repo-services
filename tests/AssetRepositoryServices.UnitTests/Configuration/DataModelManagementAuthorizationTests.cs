using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using SystemBlueprintsController = Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers.BlueprintsController;
using SystemCkModelCatalogController = Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers.CkModelCatalogController;
using TenantBlueprintsController = Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers.BlueprintsController;
using TenantModelsController = Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers.ModelsController;

namespace AssetRepositoryServices.UnitTests.Configuration;

/// <summary>
///     AB#8529: changing a tenant's data model needs the tenant role <c>DataModelManagement</c> on top of the
///     <c>octo_api</c> scope. Every model-changing REST endpoint carries <c>DataModelManagementPolicy</c>; a caller with
///     read-write rights but without the role gets <c>403</c> with the code <c>DataModelManagementRequired</c> and the
///     action never runs; a caller with the role passes. Evaluated with the production policy registration.
/// </summary>
public class DataModelManagementAuthorizationTests
{
    private const string ErrorCode = "DataModelManagementRequired";

    /// <summary>The endpoints behind the policy (controller, action) - the list of AC 1.</summary>
    public static TheoryData<Type, string> ModelChangingEndpoints =>
        new()
        {
            { typeof(TenantModelsController), nameof(TenantModelsController.ImportCk) },
            { typeof(TenantModelsController), nameof(TenantModelsController.ImportFromCatalog) },
            { typeof(TenantModelsController), nameof(TenantModelsController.ImportFromCatalogBatch) },
            { typeof(TenantBlueprintsController), nameof(TenantBlueprintsController.Apply) },
            { typeof(TenantBlueprintsController), nameof(TenantBlueprintsController.ApplyUpdate) },
            { typeof(TenantBlueprintsController), nameof(TenantBlueprintsController.Uninstall) },
            { typeof(SystemBlueprintsController), nameof(SystemBlueprintsController.RefreshAllCatalogs) },
            { typeof(SystemBlueprintsController), nameof(SystemBlueprintsController.RefreshCatalog) },
            { typeof(SystemCkModelCatalogController), nameof(SystemCkModelCatalogController.RefreshAll) },
            { typeof(SystemCkModelCatalogController), nameof(SystemCkModelCatalogController.RefreshCatalog) }
        };

    private static ClaimsPrincipal Caller(string? scope, params string[] roles)
    {
        var claims = new List<Claim> { new("tenant_id", "t"), new("sub", "s") };
        if (scope != null)
        {
            claims.Add(new Claim(InfrastructureCommon.ClaimScope, scope));
        }

        claims.AddRange(roles.Select(r => new Claim("role", r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer", "name", "role"));
    }

    private static string PolicyOf(Type controller, string action)
    {
        var attributes = controller.GetMethod(action)!.GetCustomAttributes<AuthorizeAttribute>().ToList();
        attributes.Should().ContainSingle();
        return attributes[0].Policy!;
    }

    private static async Task<bool> Allows(string policy, ClaimsPrincipal user)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(o => o.AddAssetRepositoryPolicies());
        services.AddOctoTenantRoleAuthorization();
        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, policy);
        return result.Succeeded;
    }

    [Theory]
    [MemberData(nameof(ModelChangingEndpoints))]
    public void Endpoint_CarriesTheDataModelManagementPolicy(Type controller, string action)
    {
        PolicyOf(controller, action).Should().Be(AssetRepositoryServiceConstants.DataModelManagementPolicy);
    }

    [Theory]
    [MemberData(nameof(ModelChangingEndpoints))]
    public async Task ReadWriteCallerWithoutTheRole_IsRefused(Type controller, string action)
    {
        (await Allows(PolicyOf(controller, action), Caller(CommonConstants.OctoApiFullAccess))).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(ModelChangingEndpoints))]
    public async Task OtherAdministratorRoles_AreNotEnough(Type controller, string action)
    {
        var user = Caller(CommonConstants.OctoApiFullAccess, CommonConstants.TenantManagementRole,
            CommonConstants.AdminPanelManagementRole, CommonConstants.UserManagementRole);

        (await Allows(PolicyOf(controller, action), user)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(ModelChangingEndpoints))]
    public async Task CallerWithScopeAndDataModelManagement_IsAllowed(Type controller, string action)
    {
        var user = Caller(CommonConstants.OctoApiFullAccess, CommonConstants.DataModelManagementRole);

        (await Allows(PolicyOf(controller, action), user)).Should().BeTrue();
    }

    [Fact]
    public async Task RoleWithoutFullAccessScope_IsRefused()
    {
        var user = Caller(CommonConstants.OctoApiReadOnly, CommonConstants.DataModelManagementRole);

        (await Allows(AssetRepositoryServiceConstants.DataModelManagementPolicy, user)).Should().BeFalse();
    }

    /// <summary>
    ///     The former scope-only policy accepted <c>octo_api.data_model_management</c> without <c>octo_api</c>; the role
    ///     policy does not (AC 1: scope <c>octo_api</c> AND the role).
    /// </summary>
    [Fact]
    public async Task DataModelManagementScopeAlone_IsRefused()
    {
        var user = Caller(CommonConstants.OctoApiDataModelManagement, CommonConstants.DataModelManagementRole);

        (await Allows(AssetRepositoryServiceConstants.DataModelManagementPolicy, user)).Should().BeFalse();
    }

    [Fact]
    public async Task RoleAsInboundMappedRoleClaim_IsRecognised()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess),
            new Claim(ClaimTypes.Role, CommonConstants.DataModelManagementRole)
        ], "Bearer", "name", "role"));

        (await Allows(AssetRepositoryServiceConstants.DataModelManagementPolicy, user)).Should().BeTrue();
    }

    [Fact]
    public async Task WithoutTheRole_Is403WithMessageAndCode_AndTheActionNeverRuns()
    {
        await using var host = await GateHost.StartAsync();

        var response = await host.PostAsync(roles: CommonConstants.TenantManagementRole);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        json.RootElement.GetProperty("code").GetString().Should().Be(ErrorCode);
        json.RootElement.GetProperty("message").GetString().Should().Contain(CommonConstants.DataModelManagementRole);
        host.Executions.Should().Be(0);
    }

    [Fact]
    public async Task WithTheRole_PassesAuthorization()
    {
        await using var host = await GateHost.StartAsync();

        var response = await host.PostAsync(roles: CommonConstants.DataModelManagementRole);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Executions.Should().Be(1);
    }

    private sealed class TestBearerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TestBearer";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim> { new("sub", "user-1"), new("scope", CommonConstants.OctoApiFullAccess) };
            claims.AddRange(Request.Headers["X-Roles"].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim("role", r)));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer", "name", "role")), SchemeName)));
        }
    }

    private sealed class GateHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private HttpClient? _client;
        private int _executions;

        private GateHost(WebApplication app)
        {
            _app = app;
        }

        public int Executions => _executions;

        public static async Task<GateHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddAuthentication(TestBearerHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestBearerHandler>(TestBearerHandler.SchemeName, null);
            builder.Services.AddAuthorization(o => o.AddAssetRepositoryPolicies());
            builder.Services.AddOctoTenantRoleAuthorization();

            var app = builder.Build();
            var host = new GateHost(app);
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapPost("/probe", () =>
            {
                Interlocked.Increment(ref host._executions);
                return Results.Ok();
            }).RequireAuthorization(AssetRepositoryServiceConstants.DataModelManagementPolicy);

            await app.StartAsync();
            host._client = app.GetTestClient();
            return host;
        }

        public Task<HttpResponseMessage> PostAsync(string roles)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/probe");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer not-a-real-token");
            request.Headers.TryAddWithoutValidation("X-Roles", roles);
            return _client!.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _client?.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
