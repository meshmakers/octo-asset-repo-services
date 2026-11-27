using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using GraphQL;
using GraphQL.Server;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AssetRepositoryServices.UnitTests.Configuration;

/// <summary>
///     AB#6832 (Lane B of AB#6728) — negative tests of the user-token tenant gate, driven through the
///     <b>real</b> <c>UseOctoTenantAuthorization()</c> pipeline, the real <c>{tenantId:tenantId}</c> route
///     constraint and the real option registration order of <c>Program.cs</c>. Only three things are
///     stand-ins: the authentication handler (it issues a principal labelled <c>Bearer</c>, which is
///     exactly what <c>ConfigureJwtBearerOptions</c> produces and what the wiring tests pin), and the
///     GraphQL schema and the REST controller behind the gate (a one-field schema and a one-action
///     controller whose only job is to count executions). The question answered here is therefore
///     narrow and exact: does a request of a foreign tenant ever reach a resolver or an action once the
///     mode is <c>Enforce</c>?
/// </summary>
public class TenantGatePipelineTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private const string GraphQlRoute = "/tenants/{0}/graphQl";
    private const string RestRoute = "/{0}/v1/probe";
    private const string ScopedRestRoute = "/{0}/v1/probe/scoped";

    // ---- GraphQL ---------------------------------------------------------------------------------

    [Fact]
    public async Task Enforce_GraphQl_TokenOfTenantAOnTenantBRoute_Is403AndResolverDoesNotRun()
    {
        await using var host = await GateHost.StartAsync(Enforce());

        var response = await host.PostGraphQlAsync(TenantB, TenantA);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertDeniedBodyAsync(response, TenantA, TenantB);
        host.Probe.Executions.Should().Be(0, "a refused request must not execute the resolver");
    }

    [Fact]
    public async Task Enforce_GraphQl_TokenOfMatchingTenant_PassesAndResolverRuns()
    {
        await using var host = await GateHost.StartAsync(Enforce());

        var response = await host.PostGraphQlAsync(TenantB, TenantB);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Probe.Executions.Should().Be(1);
    }

    [Fact]
    public async Task Enforce_GraphQl_TokenWithoutTenantId_Is403AndResolverDoesNotRun()
    {
        await using var host = await GateHost.StartAsync(Enforce());

        var response = await host.PostGraphQlAsync(TenantB, tokenTenant: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        host.Probe.Executions.Should().Be(0);
    }

    // ---- REST ------------------------------------------------------------------------------------

    [Fact]
    public async Task Enforce_Rest_TokenOfTenantAOnTenantBRoute_Is403AndActionDoesNotRun()
    {
        await using var host = await GateHost.StartAsync(Enforce());

        var response = await host.GetRestAsync(TenantB, TenantA);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertDeniedBodyAsync(response, TenantA, TenantB);
        host.Probe.Executions.Should().Be(0, "a refused request must not execute the controller action");
    }

    [Fact]
    public async Task Enforce_Rest_TokenOfMatchingTenant_PassesAndActionRuns()
    {
        await using var host = await GateHost.StartAsync(Enforce());

        var response = await host.GetRestAsync(TenantB, TenantB);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Probe.Executions.Should().Be(1);
    }

    [Fact]
    public async Task Enforce_Rest_TokenWithoutTenantId_Is403AndActionDoesNotRun()
    {
        await using var host = await GateHost.StartAsync(Enforce());

        var response = await host.GetRestAsync(TenantB, tokenTenant: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        host.Probe.Executions.Should().Be(0);
    }

    // ---- agent tokens -------------------------------------------------------------------------------

    /// <summary>
    ///     A token with scope <c>octo_agent</c> could read tenant data through GraphQL queries, which require no scope.
    ///     Deny by default: an agent token of the route's own tenant gets <c>403 AgentTokenNotAccepted</c> on GraphQL
    ///     (query and mutation) and REST, in every mode of the tenant gate, and nothing runs.
    /// </summary>
    [Theory]
    [InlineData("Enforce", "octo_agent")]
    [InlineData("LogOnly", "octo_agent")]
    [InlineData("Enforce", "octo_agent octo_api")]
    public async Task AgentTokenOfTheRouteTenant_GraphQlQueryMutationAndRest_Are403AgentTokenNotAccepted(string mode,
        string scope)
    {
        await using var host = await GateHost.StartAsync(new Dictionary<string, string?>
        {
            ["TenantAuthorization:UserTokenEnforcement"] = mode,
            ["TenantAuthorization:ServiceTokenEnforcement"] = mode
        });

        var query = await host.PostGraphQlAsync(TenantB, TenantB, scope, serviceToken: true);
        var mutation = await host.PostGraphQlAsync(TenantB, TenantB, scope, serviceToken: true,
            body: "{\"query\":\"mutation { probe }\"}");
        var rest = await host.GetRestAsync(TenantB, TenantB, scope, serviceToken: true);
        var scopedRest = await host.GetRestAsync(TenantB, TenantB, scope, serviceToken: true, scoped: true);

        foreach (var response in new[] { query, mutation, rest, scopedRest })
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            using var json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            json.RootElement.GetProperty("code").GetString().Should().Be("AgentTokenNotAccepted");
        }

        host.Probe.Executions.Should().Be(0, "an agent token must not reach a resolver or an action");
    }

    /// <summary>
    ///     A REST action with the production scope policy <c>TenantAssetApiReadOnlyPolicy</c> (octo_api or
    ///     octo_api_readonly): <c>UseAuthorization()</c> refuses an agent token before the tenant gate runs, and the result
    ///     handler of <c>AddOctoTenantRoleAuthorization()</c> still answers with the agent code, not an empty 403.
    /// </summary>
    [Fact]
    public async Task AgentToken_OnARestActionWithTheProductionScopePolicy_Is403AgentTokenNotAccepted()
    {
        await using var host = await GateHost.StartAsync(Enforce());

        var response = await host.GetRestAsync(TenantB, TenantB, "octo_agent", serviceToken: true, scoped: true);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        json.RootElement.GetProperty("code").GetString().Should().Be(AgentTokens.NotAcceptedErrorCode);
        host.Probe.Executions.Should().Be(0);
    }

    [Fact]
    public async Task PlatformClientTokenOfTheRouteTenant_IsNotAffected()
    {
        await using var host = await GateHost.StartAsync(Enforce());

        (await host.PostGraphQlAsync(TenantB, TenantB, "octo_api", serviceToken: true)).StatusCode.Should()
            .Be(HttpStatusCode.OK);
        (await host.GetRestAsync(TenantB, TenantB, "octo_api", serviceToken: true)).StatusCode.Should()
            .Be(HttpStatusCode.OK);
        (await host.GetRestAsync(TenantB, TenantB, "octo_api", serviceToken: true, scoped: true)).StatusCode.Should()
            .Be(HttpStatusCode.OK);
        (await host.PostGraphQlAsync(TenantB, TenantB, "octo_api", serviceToken: true,
            body: "{\"query\":\"mutation { probe }\"}")).StatusCode.Should().Be(HttpStatusCode.OK);
        host.Probe.Executions.Should().Be(4);
    }

    // ---- the mode is what flips it ---------------------------------------------------------------

    /// <summary>
    ///     The contrast that gives the tests above their meaning: with the code default alone (LogOnly,
    ///     no configuration) the very same foreign-tenant requests reach the handlers. Together they show
    ///     that the helm value is the only thing between today's behaviour and a refusal.
    /// </summary>
    [Fact]
    public async Task LogOnlyCodeDefault_ForeignTenantRequestsStillReachTheHandlers()
    {
        await using var host = await GateHost.StartAsync(new Dictionary<string, string?>());

        (await host.PostGraphQlAsync(TenantB, TenantA)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.GetRestAsync(TenantB, TenantA)).StatusCode.Should().Be(HttpStatusCode.OK);
        host.Probe.Executions.Should().Be(2);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static Dictionary<string, string?> Enforce() => new()
    {
        ["TenantAuthorization:UserTokenEnforcement"] = "Enforce"
    };

    private static async Task AssertDeniedBodyAsync(HttpResponseMessage response, string tokenTenant,
        string routeTenant)
    {
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var message = json.RootElement.GetProperty("message").GetString();
        message.Should().Contain(routeTenant).And.Contain(tokenTenant);
    }

    public sealed class ProbeCounter
    {
        private int _executions;
        public int Executions => _executions;
        public void Hit() => Interlocked.Increment(ref _executions);
    }

    private sealed class ProbeQuery : ObjectGraphType
    {
        public ProbeQuery(ProbeCounter probe)
        {
            Name = "Query";
            Field<StringGraphType>("probe").Resolve(_ =>
            {
                probe.Hit();
                return "ok";
            });
        }
    }

    private sealed class ProbeMutation : ObjectGraphType
    {
        public ProbeMutation(ProbeCounter probe)
        {
            Name = "Mutation";
            Field<StringGraphType>("probe").Resolve(_ =>
            {
                probe.Hit();
                return "ok";
            });
        }
    }

    private sealed class ProbeSchema : Schema
    {
        public ProbeSchema(IServiceProvider provider, ProbeQuery query, ProbeMutation mutation) : base(provider)
        {
            Query = query;
            Mutation = mutation;
        }
    }

    /// <summary>Limits MVC discovery to the probe controller (this assembly holds no other).</summary>
    private sealed class ProbeControllerPart : ApplicationPart, IApplicationPartTypeProvider
    {
        public override string Name => nameof(ProbeControllerPart);
        public IEnumerable<System.Reflection.TypeInfo> Types => [typeof(ProbeController).GetTypeInfo()];
    }

    /// <summary>
    ///     Principal labelled <c>Bearer</c>, built from test headers; the <c>Authorization</c> header only
    ///     has to start with <c>Bearer </c> for the gate to look at the request (fake value, no token).
    /// </summary>
    private sealed class TestBearerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TestBearer";
        public const string TenantHeader = "X-Test-Tenant";
        public const string ScopeHeader = "X-Test-Scope";
        public const string ServiceTokenHeader = "X-Test-Service-Token";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim> { new("client_id", "test-client") };
            if (!Request.Headers.ContainsKey(ServiceTokenHeader))
            {
                claims.Add(new Claim("sub", "user-1"));
            }

            if (Request.Headers.TryGetValue(TenantHeader, out var tenant) && tenant.Count > 0)
            {
                claims.Add(new Claim("tenant_id", tenant.ToString()));
            }

            if (Request.Headers.TryGetValue(ScopeHeader, out var scope) && scope.Count > 0)
            {
                claims.Add(new Claim("scope", scope.ToString()));
            }

            var identity = new ClaimsIdentity(claims, "Bearer");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    private sealed class GateHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;

        private GateHost(WebApplication app, ProbeCounter probe)
        {
            _app = app;
            Probe = probe;
            _client = app.GetTestClient();
        }

        public ProbeCounter Probe { get; }

        /// <param name="configuration">What the operator sets (helm value / environment) as configuration.</param>
        public static async Task<GateHost> StartAsync(Dictionary<string, string?> configuration)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(configuration);

            // Same two calls in the same order as Program.cs (pinned against the real source elsewhere).
            builder.Services.AddOctoTenantAuthorization(o =>
                o.UserTokenEnforcement = UserTokenTenantEnforcementMode.LogOnly);
            builder.Services.AddOctoTenantAuthorization(builder.Configuration);
            builder.Services.AddOctoTenantIdRouteConstraint();

            var probe = new ProbeCounter();
            builder.Services.AddSingleton(probe);
            builder.Services.AddSingleton<ProbeQuery>();
            builder.Services.AddSingleton<ProbeMutation>();
            builder.Services.AddGraphQL(b => b.AddSchema<ProbeSchema>().AddSystemTextJson());
            builder.Services.AddControllers()
                .ConfigureApplicationPartManager(m =>
                {
                    m.ApplicationParts.Clear();
                    m.ApplicationParts.Add(new ProbeControllerPart());
                });
            builder.Services.AddAuthentication(TestBearerHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestBearerHandler>(TestBearerHandler.SchemeName, null);
            // The production policies and result handler (RuntimeEngineBuilderExtensions).
            builder.Services.AddAuthorization(o => o.AddAssetRepositoryPolicies());
            builder.Services.AddOctoTenantRoleAuthorization();

            var app = builder.Build();

            // Same order as OctoApplicationBuilderExtensions.ConfigureOcto.
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseOctoTenantAuthorization();
            app.MapControllers().RequireAuthorization();
            app.MapGraphQL<ProbeSchema>("tenants/{tenantId:tenantId}/graphQl").RequireAuthorization();

            await app.StartAsync();
            return new GateHost(app, probe);
        }

        public Task<HttpResponseMessage> PostGraphQlAsync(string routeTenant, string? tokenTenant, string? scope = null,
            bool serviceToken = false, string body = "{\"query\":\"{ probe }\"}")
        {
            var request = Request(HttpMethod.Post, string.Format(GraphQlRoute, routeTenant), tokenTenant, scope,
                serviceToken);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return _client.SendAsync(request);
        }

        public Task<HttpResponseMessage> GetRestAsync(string routeTenant, string? tokenTenant, string? scope = null,
            bool serviceToken = false, bool scoped = false)
        {
            return _client.SendAsync(Request(HttpMethod.Get,
                string.Format(scoped ? ScopedRestRoute : RestRoute, routeTenant), tokenTenant, scope, serviceToken));
        }

        private static HttpRequestMessage Request(HttpMethod method, string path, string? tokenTenant, string? scope,
            bool serviceToken)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer not-a-real-token");
            if (tokenTenant != null)
            {
                request.Headers.TryAddWithoutValidation(TestBearerHandler.TenantHeader, tokenTenant);
            }

            if (scope != null)
            {
                request.Headers.TryAddWithoutValidation(TestBearerHandler.ScopeHeader, scope);
            }

            if (serviceToken)
            {
                request.Headers.TryAddWithoutValidation(TestBearerHandler.ServiceTokenHeader, "1");
            }

            return request;
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}

/// <summary>
///     One-action controller behind the gate (AB#6832). Top level on purpose: MVC discovery ignores nested
///     controller types.
/// </summary>
[ApiController]
[Route("{tenantId:tenantId}/v1/probe")]
public sealed class ProbeController(TenantGatePipelineTests.ProbeCounter probe) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        probe.Hit();
        return Ok(new { ok = true });
    }

    /// <summary>An action with the scope policy of the real tenant REST reads.</summary>
    [HttpGet("scoped")]
    [Microsoft.AspNetCore.Authorization.Authorize(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy)]
    public IActionResult GetScoped()
    {
        probe.Hit();
        return Ok(new { ok = true });
    }
}
