using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AssetRepositoryServices.UnitTests.Configuration;

/// <summary>
///     AB#8709: every REST endpoint of <see cref="StreamDataController" /> that changes a stream-data archive or its
///     columns needs the tenant role <c>StreamDataAdmin</c> on top of the <c>octo_api</c> scope - the same rule as the
///     <c>streamData</c> GraphQL mutations and the Archive CK methods. Before the fix they carried the scope-only
///     <c>TenantAssetApiReadWritePolicy</c>, so a client with <c>octo_api</c> and no role could activate, disable and
///     delete archives (reproduced on test-2). Evaluated with the production policy registration, per endpoint group:
///     a caller with the scope only is refused, a caller with scope and role passes.
/// </summary>
public class StreamDataAdministrationAuthorizationTests
{
    private const string ErrorCode = "StreamDataAdminRequired";

    /// <summary>Archive lifecycle: activate, disable, enable, retry, delete.</summary>
    public static TheoryData<string> ArchiveLifecycleEndpoints =>
        new()
        {
            nameof(StreamDataController.ActivateArchive),
            nameof(StreamDataController.DisableArchive),
            nameof(StreamDataController.EnableArchive),
            nameof(StreamDataController.RetryArchiveActivation),
            nameof(StreamDataController.DeleteArchive)
        };

    /// <summary>Rollup operations: freeze, unfreeze, rewind, recompute, backfill from source, repair.</summary>
    public static TheoryData<string> RollupEndpoints =>
        new()
        {
            nameof(StreamDataController.FreezeRollupArchive),
            nameof(StreamDataController.UnfreezeRollupArchive),
            nameof(StreamDataController.RewindRollupWatermark),
            nameof(StreamDataController.RecomputeArchive),
            nameof(StreamDataController.BackfillRollupFromSource),
            nameof(StreamDataController.RepairRollups)
        };

    /// <summary>Column changes: computed columns (add, remove, formula, retry, repair) and ingested columns (add).</summary>
    public static TheoryData<string> ColumnEndpoints =>
        new()
        {
            nameof(StreamDataController.AddComputedColumn),
            nameof(StreamDataController.AddArchiveColumn),
            nameof(StreamDataController.RemoveComputedColumn),
            nameof(StreamDataController.UpdateComputedColumnFormula),
            nameof(StreamDataController.RetryComputedColumnBackfill),
            nameof(StreamDataController.RepairComputedColumn)
        };

    /// <summary>
    ///     Write actions that deliberately do not carry the StreamDataAdmin policy: the tenant-wide enable / disable
    ///     (TenantManagement, AB#4497) and the row writes (scope for automated points, StreamDataAdmin checked in the
    ///     action for manual points, AB#8590).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> OtherWriteActions = new Dictionary<string, string>
    {
        [nameof(StreamDataController.Enable)] = AssetRepositoryServiceConstants.TenantAdministrationPolicy,
        [nameof(StreamDataController.Disable)] = AssetRepositoryServiceConstants.TenantAdministrationPolicy,
        [nameof(StreamDataController.Insert)] = AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy,
        [nameof(StreamDataController.InsertTimeRange)] = AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy
    };

    private static ClaimsPrincipal Caller(string? scope, params string[] roles)
    {
        var claims = new List<Claim> { new("tenant_id", "t"), new("client_id", "ci-client") };
        if (scope != null)
        {
            claims.Add(new Claim(InfrastructureCommon.ClaimScope, scope));
        }

        claims.AddRange(roles.Select(r => new Claim("role", r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer", "name", "role"));
    }

    private static string PolicyOf(string action)
    {
        var attributes = typeof(StreamDataController).GetMethod(action)!.GetCustomAttributes<AuthorizeAttribute>()
            .ToList();
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

    private static async Task AssertScopeOnlyRefusedAndAdminAllowed(string action)
    {
        var policy = PolicyOf(action);
        policy.Should().Be(AssetRepositoryServiceConstants.StreamDataAdministrationPolicy);

        (await Allows(policy, Caller(CommonConstants.OctoApiFullAccess))).Should().BeFalse(
            "a caller with the octo_api scope but without StreamDataAdmin must not change archives ({0})", action);
        (await Allows(policy, Caller(CommonConstants.OctoApiFullAccess, CommonConstants.StreamDataAdminRole)))
            .Should().BeTrue("StreamDataAdmin with the octo_api scope is the documented right ({0})", action);
    }

    [Theory]
    [MemberData(nameof(ArchiveLifecycleEndpoints))]
    public Task ArchiveLifecycle_ScopeOnlyIsRefused_AdminIsAllowed(string action)
        => AssertScopeOnlyRefusedAndAdminAllowed(action);

    [Theory]
    [MemberData(nameof(RollupEndpoints))]
    public Task RollupOperations_ScopeOnlyIsRefused_AdminIsAllowed(string action)
        => AssertScopeOnlyRefusedAndAdminAllowed(action);

    [Theory]
    [MemberData(nameof(ColumnEndpoints))]
    public Task ColumnChanges_ScopeOnlyIsRefused_AdminIsAllowed(string action)
        => AssertScopeOnlyRefusedAndAdminAllowed(action);

    [Theory]
    [InlineData(CommonConstants.StreamDataWriterRole)]
    [InlineData(CommonConstants.StreamDataReaderRole)]
    [InlineData(CommonConstants.TenantManagementRole)]
    [InlineData(CommonConstants.DataModelManagementRole)]
    public async Task OtherRoles_AreNotEnough(string role)
    {
        (await Allows(AssetRepositoryServiceConstants.StreamDataAdministrationPolicy,
            Caller(CommonConstants.OctoApiFullAccess, role))).Should().BeFalse();
    }

    [Fact]
    public async Task RoleWithReadOnlyScope_IsRefused()
    {
        (await Allows(AssetRepositoryServiceConstants.StreamDataAdministrationPolicy,
            Caller(CommonConstants.OctoApiReadOnly, CommonConstants.StreamDataAdminRole))).Should().BeFalse();
    }

    [Fact]
    public async Task RoleAsInboundMappedRoleClaim_IsRecognised()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess),
            new Claim(ClaimTypes.Role, CommonConstants.StreamDataAdminRole)
        ], "Bearer", "name", "role"));

        (await Allows(AssetRepositoryServiceConstants.StreamDataAdministrationPolicy, user)).Should().BeTrue();
    }

    /// <summary>
    ///     Guard against the next scope-only write endpoint: every non-GET action of the controller either carries the
    ///     StreamDataAdmin policy or is one of the listed exceptions with its own rule.
    /// </summary>
    [Fact]
    public void EveryWriteAction_CarriesStreamDataAdministration_OrIsAListedException()
    {
        var listed = ArchiveLifecycleEndpoints.Concat(RollupEndpoints).Concat(ColumnEndpoints)
            .Select(row => row.Data).ToHashSet();

        var writeActions = typeof(StreamDataController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>()
                .Any(a => a.HttpMethods.Any(verb => verb != HttpMethods.Get)))
            .ToList();

        writeActions.Should().NotBeEmpty();
        foreach (var action in writeActions)
        {
            var policy = PolicyOf(action.Name);
            if (OtherWriteActions.TryGetValue(action.Name, out var expected))
            {
                policy.Should().Be(expected, "{0} has its own documented rule", action.Name);
            }
            else
            {
                policy.Should().Be(AssetRepositoryServiceConstants.StreamDataAdministrationPolicy,
                    "{0} changes a stream-data archive or its columns (AB#8709)", action.Name);
                listed.Should().Contain(action.Name, "every archive-changing endpoint is in an endpoint group");
            }
        }
    }

    [Fact]
    public async Task ScopeOnly_Is403WithMessageAndCode_AndTheActionNeverRuns()
    {
        await using var host = await GateHost.StartAsync();

        var response = await host.PostAsync(roles: "");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        json.RootElement.GetProperty("code").GetString().Should().Be(ErrorCode);
        json.RootElement.GetProperty("message").GetString().Should().Contain(CommonConstants.StreamDataAdminRole);
        host.Executions.Should().Be(0);
    }

    [Fact]
    public async Task WithTheRole_PassesAuthorization()
    {
        await using var host = await GateHost.StartAsync();

        var response = await host.PostAsync(roles: CommonConstants.StreamDataAdminRole);

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
            var claims = new List<Claim>
                { new("client_id", "ci-client"), new("scope", CommonConstants.OctoApiFullAccess) };
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
            }).RequireAuthorization(AssetRepositoryServiceConstants.StreamDataAdministrationPolicy);

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
