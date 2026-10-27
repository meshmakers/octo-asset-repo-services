using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace AssetRepositoryServices.UnitTests.Adversarial;

/// <summary>
///     AB#6915, HTTP layer of the method path: attack tokens against the <b>real</b> JWT bearer configuration of this
///     service (<c>ConfigureJwtBearerOptions</c>, signature and issuer validated against a fake identity service), the real
///     tenant gate in mode <c>Enforce</c> and the real method authorization evaluator behind it. The endpoint stands in for
///     a method mutation: it builds the principal from the validated token exactly like the gateway and asks the evaluator
///     for a method that declares the role <c>AssetManagement</c>. A refused request must never reach the counter.
/// </summary>
public class BearerTokenAttackTests
{
    private const string Tenant = "meshtest";
    private const string Authority = "https://identity.adversarial.test/";
    private const string User = "65f0c0ffee65f0c0ffee65a1";
    private const string Target = "65f0c0ffee65f0c0ffee65f0";

    private static readonly CkMethodAuthorizationDto Declared = new() { Roles = ["AssetManagement"] };

    private sealed class Probe
    {
        private int _hits;
        public int Hits => _hits;
        public void Hit() => Interlocked.Increment(ref _hits);
    }

    private sealed class FakeIdentity(RSA rsa, string kid) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                json = $"{{\"issuer\":\"{Authority}\",\"jwks_uri\":\"{Authority}.well-known/openid-configuration/jwks\"}}";
            }
            else if (path.EndsWith("/jwks", StringComparison.Ordinal))
            {
                var p = rsa.ExportParameters(false);
                json = "{\"keys\":[{\"kty\":\"RSA\",\"use\":\"sig\",\"alg\":\"RS256\",\"kid\":\"" + kid + "\",\"n\":\"" +
                       Base64UrlEncoder.Encode(p.Modulus) + "\",\"e\":\"" + Base64UrlEncoder.Encode(p.Exponent) + "\"}]}";
            }
            else
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;
        private readonly RSA _rsa = RSA.Create(2048);

        private Rig(WebApplication app, Probe probe, RSA rsa, SigningCredentials signing)
        {
            _app = app;
            Probe = probe;
            _client = app.GetTestClient();
            _rsa = rsa;
            Signing = signing;
        }

        public Probe Probe { get; }

        public SigningCredentials Signing { get; }

        public static async Task<Rig> StartAsync(string userTokenEnforcement = "Enforce")
        {
            var rsa = RSA.Create(2048);
            var signing = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "adv-key" }, SecurityAlgorithms.RsaSha256);
            var identity = new FakeIdentity(rsa, "adv-key");
            var probe = new Probe();

            // the same registrations as Program.cs / RuntimeEngineBuilderExtensions, in the same order
            JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TenantAuthorization:UserTokenEnforcement"] = userTokenEnforcement
            });
            builder.Services.AddOctoTenantAuthorization(o => o.UserTokenEnforcement = UserTokenTenantEnforcementMode.LogOnly);
            builder.Services.AddOctoTenantAuthorization(builder.Configuration);
            builder.Services.AddOctoTenantIdRouteConstraint();
            builder.Services.Configure<OctoAssetRepositoryServicesOptions>(o => o.Authority = Authority);
            builder.Services.ConfigureOptions<ConfigureJwtBearerOptions>();
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
            // Configure, not PostConfigure: the backchannel is built in the post-configuration of the JWT bearer options
            builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
                o => o.BackchannelHttpHandler = identity);
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton(probe);
            builder.Services.AddSingleton<ICkMethodAuthorizationEvaluator>(
                new CkMethodAuthorizationEvaluator(Options.Create(new CkMethodAuthorizationOptions())));

            var app = builder.Build();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseOctoTenantAuthorization();
            app.MapPost("/{tenantId:tenantId}/v1/method",
                (HttpContext context, ICkMethodAuthorizationEvaluator evaluator, Probe counter) =>
                {
                    var principal = CkMethodPrincipal.FromClaimsPrincipal(context.User);
                    var decision = evaluator.Evaluate(Declared, CkMethodKindDto.Instance, Target, principal);
                    if (!decision.Allowed)
                    {
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                    }

                    counter.Hit();
                    return Results.Ok();
                }).RequireAuthorization();
            await app.StartAsync();
            return new Rig(app, probe, rsa, signing);
        }

        public string Token(string subject = User, string tenant = Tenant, string? audience = null, bool scope = true,
            DateTime? expires = null, string issuer = Authority, string[]? roles = null, SigningCredentials? signing = null,
            bool serviceToken = false)
        {
            var claims = new Dictionary<string, object> { ["client_id"] = serviceToken ? "mesh-bot-job" : "studio" };
            if (!serviceToken)
            {
                claims["sub"] = subject;
            }

            if (tenant.Length > 0)
            {
                claims["tenant_id"] = tenant;
            }

            claims["role"] = roles ?? ["AssetManagement"];
            if (scope)
            {
                claims["scope"] = new[] { "octo_api", "openid" };
            }

            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = audience,
                Claims = claims,
                NotBefore = DateTime.UtcNow.AddMinutes(-10),
                Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
                SigningCredentials = signing ?? Signing
            });
        }

        public async Task<HttpStatusCode> PostAsync(string? token, string routeTenant = Tenant)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/{routeTenant}/v1/method");
            if (token != null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            }

            using var response = await _client.SendAsync(request);
            return response.StatusCode;
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            _rsa.Dispose();
        }
    }

    private static string Unsigned(string tenant = Tenant)
    {
        var header = Base64UrlEncoder.Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var payload = Base64UrlEncoder.Encode(
            $"{{\"iss\":\"{Authority}\",\"sub\":\"{User}\",\"tenant_id\":\"{tenant}\",\"role\":\"AssetManagement\",\"scope\":\"octo_api\",\"exp\":{exp}}}");
        return header + "." + payload + ".";
    }

    private static void Record(string caseId, string attack, HttpStatusCode status, Probe probe,
        [CallerMemberName] string test = "") =>
        AdversarialCaseLog.Record(caseId, attack, "BearerTokenAttackTests." + test, $"HTTP {(int)status}",
            probe.Hits == 0 ? "none (endpoint not reached)" : $"ENDPOINT RAN {probe.Hits}x");

    [Fact]
    public async Task C00_Control_AValidToken_WithTheRole_ReachesTheMethodEndpoint()
    {
        await using var rig = await Rig.StartAsync();

        var status = await rig.PostAsync(rig.Token());

        Record("C00", "control: valid token (tenant, role, octo_api)", status, rig.Probe);
        status.Should().Be(HttpStatusCode.OK);
        rig.Probe.Hits.Should().Be(1);
    }

    [Fact]
    public async Task C02_TokenOfAnotherTenant_IsRefusedByTheTenantGate()
    {
        await using var rig = await Rig.StartAsync();

        var status = await rig.PostAsync(rig.Token(tenant: "othertenant"));
        var noTenant = await rig.PostAsync(rig.Token(tenant: string.Empty));

        Record("C02", "token of another tenant on the route of tenant meshtest (gate Enforce)", status, rig.Probe);
        Record("C02", "token without tenant_id (gate Enforce)", noTenant, rig.Probe);
        status.Should().Be(HttpStatusCode.Forbidden);
        noTenant.Should().Be(HttpStatusCode.Forbidden);
        rig.Probe.Hits.Should().Be(0);
    }

    [Fact]
    public async Task C02b_Limitation_UnderLogOnly_TheTenantGateDoesNotRefuseAForeignTenantToken()
    {
        // The code default of this service (Program.cs) is LogOnly until the chart flips it (AB#6728). The method gateway
        // does not compare the token tenant with the route tenant itself, so it inherits this mode.
        await using var rig = await Rig.StartAsync(userTokenEnforcement: "LogOnly");

        var status = await rig.PostAsync(rig.Token(tenant: "othertenant"));

        Record("C02b", "LIMITATION L-3: token of another tenant, gate in the code default LogOnly", status, rig.Probe);
        status.Should().Be(HttpStatusCode.OK);
        rig.Probe.Hits.Should().Be(1);
    }

    [Fact]
    public async Task C03_ExpiredToken_Is401()
    {
        await using var rig = await Rig.StartAsync();

        var status = await rig.PostAsync(rig.Token(expires: DateTime.UtcNow.AddMinutes(-5)));

        Record("C03", "expired token", status, rig.Probe);
        status.Should().Be(HttpStatusCode.Unauthorized);
        rig.Probe.Hits.Should().Be(0);
    }

    [Fact]
    public async Task C04_Limitation_AnotherAudience_IsAccepted_BecauseTheServiceDoesNotValidateTheAudience()
    {
        await using var rig = await Rig.StartAsync();

        var status = await rig.PostAsync(rig.Token(audience: "another-api"));

        Record("C04", "LIMITATION L-2: token for another audience (ValidateAudience is off platform-wide, documented)", status, rig.Probe);
        status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task C05_WrongSignature_Is401()
    {
        await using var rig = await Rig.StartAsync();
        using var foreign = RSA.Create(2048);
        var foreignSigning = new SigningCredentials(new RsaSecurityKey(foreign) { KeyId = "adv-key" }, SecurityAlgorithms.RsaSha256);
        var valid = rig.Token();
        var parts = valid.Split('.');
        var payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1]))
            .Replace("\"AssetManagement\"", "\"AdminPanelManagement\"", StringComparison.Ordinal);
        parts[1] = Base64UrlEncoder.Encode(payload);

        var sameKid = await rig.PostAsync(rig.Token(signing: foreignSigning));
        var tampered = await rig.PostAsync(string.Join('.', parts));

        Record("C05", "wrong signature (foreign key, same kid)", sameKid, rig.Probe);
        Record("C05", "wrong signature (payload changed, signature kept)", tampered, rig.Probe);
        sameKid.Should().Be(HttpStatusCode.Unauthorized);
        tampered.Should().Be(HttpStatusCode.Unauthorized);
        rig.Probe.Hits.Should().Be(0);
    }

    [Fact]
    public async Task C06_AlgNone_AndKeyConfusion_Are401()
    {
        await using var rig = await Rig.StartAsync();
        var secret = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("a-secret-an-attacker-guessed-0123456789")) { KeyId = "adv-key" };
        var hs256 = rig.Token(signing: new SigningCredentials(secret, SecurityAlgorithms.HmacSha256));

        var none = await rig.PostAsync(Unsigned());
        var symmetric = await rig.PostAsync(hs256);

        Record("C06", "alg: none", none, rig.Probe);
        Record("C06", "alg HS256 with a guessed secret and the real kid", symmetric, rig.Probe);
        none.Should().Be(HttpStatusCode.Unauthorized);
        symmetric.Should().Be(HttpStatusCode.Unauthorized);
        rig.Probe.Hits.Should().Be(0);
    }

    [Fact]
    public async Task C07_TokenWithoutOctoApi_PassesTheHttpLayer_ButTheMethodAuthorizationRefuses()
    {
        await using var rig = await Rig.StartAsync();

        var status = await rig.PostAsync(rig.Token(scope: false));

        Record("C07", "validly signed token without octo_api, role present", status, rig.Probe);
        status.Should().Be(HttpStatusCode.Forbidden); // by the method authorization evaluator, not by the JWT layer
        rig.Probe.Hits.Should().Be(0);
    }

    [Fact]
    public async Task C07b_WrongIssuer_AndNoToken_Are401()
    {
        await using var rig = await Rig.StartAsync();

        var issuer = await rig.PostAsync(rig.Token(issuer: "https://evil.test/"));
        var none = await rig.PostAsync(null);

        Record("C07b", "token of another issuer", issuer, rig.Probe);
        Record("C07b", "no Authorization header", none, rig.Probe);
        issuer.Should().Be(HttpStatusCode.Unauthorized);
        none.Should().Be(HttpStatusCode.Unauthorized);
        rig.Probe.Hits.Should().Be(0);
    }

    [Fact]
    public async Task C07c_ATokenWithoutTheDeclaredRole_ReachesTheEndpointButIsRefusedByTheEvaluator()
    {
        await using var rig = await Rig.StartAsync();

        var status = await rig.PostAsync(rig.Token(roles: ["Reporting"]));

        Record("C07c", "valid token without the declared role", status, rig.Probe);
        status.Should().Be(HttpStatusCode.Forbidden);
        rig.Probe.Hits.Should().Be(0);
    }
}
