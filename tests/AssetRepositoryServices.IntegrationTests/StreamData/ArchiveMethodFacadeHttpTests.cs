using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Asp.Versioning;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Meshmakers.Octo.Services.Infrastructure.Configuration.DependencyInjection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
///     The fallback to the direct lifecycle call (switch off, or a tenant whose CK model does not declare the method) opens no
///     path around authorization: the real <see cref="StreamDataController" /> behind a real MVC pipeline with the service's
///     policies keeps refusing a caller without <c>StreamDataAdmin</c> with <c>403</c> before the action runs, and lets the
///     administrator through to the direct call. Only the authentication handler is a stand-in.
/// </summary>
[Collection(ArchiveMethodFacadeCollection.Name)]
public class ArchiveMethodFacadeHttpTests(ArchiveMethodFacadeFixture fixture)
{
    [Fact]
    public async Task OnTheFallback_ACallerWithoutTheRole_Is403_AndNothingChanges_AndTheAdminStillGets204()
    {
        var archive = await CreateRawArchiveAsync();
        var facade = A.Fake<IArchiveMethodFacade>();
        A.CallTo(() => facade.IsHosted).Returns(true);
        A.CallTo(() => facade.CanServe(A<ITenantContext>._, A<string>._, A<string>._)).Returns(false);
        await using var host = await StartAsync(facade);
        var path = $"/{fixture.GetSystemContext().TenantId}/v1/streamdata/archives/{archive}/activate";

        var refused = await host.PostAsync(path, roles: "SomeOtherRole");

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SnapshotAsync(archive))!.Status.Should().Be(CkArchiveStatus.Created, "the action never ran");

        var allowed = await host.PostAsync(path, roles: CommonConstants.StreamDataAdminRole);

        allowed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SnapshotAsync(archive))!.Status.Should().Be(CkArchiveStatus.Activated, "the direct call ran");
        A.CallTo(facade).Where(call => call.Method.Name == nameof(IArchiveMethodFacade.InvokeAsync)).MustNotHaveHappened();
    }

    [Fact]
    public async Task OnTheFallback_AnAdminTokenOfAnotherTenant_Is403_ThroughTheRealPipeline_AndNothingChanges()
    {
        // no HTTP tenant gate in this host (as in LogOnly mode); the fallback guard refuses the foreign token
        var archive = await CreateRawArchiveAsync();
        var facade = A.Fake<IArchiveMethodFacade>();
        A.CallTo(() => facade.IsHosted).Returns(true);
        A.CallTo(() => facade.CanServe(A<ITenantContext>._, A<string>._, A<string>._)).Returns(false);
        await using var host = await StartAsync(facade);
        var path = $"/{fixture.GetSystemContext().TenantId}/v1/streamdata/archives/{archive}/activate";

        var refused = await host.PostAsync(path, roles: CommonConstants.StreamDataAdminRole, tokenTenant: "some-other-tenant");

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("METHOD_FORBIDDEN");
        (await SnapshotAsync(archive))!.Status.Should().Be(CkArchiveStatus.Created, "the direct call never ran");
    }

    private async Task<Host> StartAsync(IArchiveMethodFacade facade)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(fixture.GetSystemContext());
        builder.Services.AddSingleton(facade);
        // the fallback guard of the host (caller tenant, data permission, audit)
        builder.Services.AddSingleton(fixture.GetService<IServiceScopeFactory>().CreateScope().ServiceProvider
            .GetRequiredService<IArchiveMethodFallbackGuard>());
        builder.Services.AddAuthentication(TestBearerHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestBearerHandler>(TestBearerHandler.SchemeName, null);
        builder.Services.AddAuthorization(o => o.AddAssetRepositoryPolicies());
        builder.Services.AddOctoTenantRoleAuthorization();
        builder.Services.AddOctoTenantIdRouteConstraint();
        builder.Services.AddControllers().AddApplicationPart(typeof(StreamDataController).Assembly);
        builder.Services.AddApiVersioning(o =>
        {
            o.AssumeDefaultVersionWhenUnspecified = true;
            o.DefaultApiVersion = new ApiVersion(1);
        }).AddMvc();

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        return new Host(app, fixture.GetSystemContext().TenantId);
    }

    private async Task<ArchiveSnapshot?> SnapshotAsync(OctoObjectId rtId)
    {
        var systemContext = fixture.GetSystemContext();
        var tenant = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        return await tenant.GetArchiveRuntimeStore().GetAsync(rtId);
    }

    private async Task<OctoObjectId> CreateRawArchiveAsync()
    {
        var archive = new RtRawArchive
        {
            RtWellKnownName = $"HttpFallback{Guid.NewGuid():N}",
            TargetCkTypeId = fixture.TestCkTypeId,
            Status = RtCkArchiveStatusEnum.Created,
            Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
            {
                new() { Path = "Voltage", Indexed = true, Required = false }
            }
        };
        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        await repository.InsertOneRtEntityAsync(session, archive);
        await session.CommitTransactionAsync();
        return archive.RtId;
    }

    private sealed class Host(WebApplication app, string tenantId) : IAsyncDisposable
    {
        private readonly HttpClient _client = app.GetTestClient();

        public Task<HttpResponseMessage> PostAsync(string path, string roles, string? tokenTenant = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer not-a-real-token");
            request.Headers.TryAddWithoutValidation("X-Roles", roles);
            request.Headers.TryAddWithoutValidation("X-Tenant", tokenTenant ?? tenantId);
            return _client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    /// <summary>The scheme the controller names (<c>Bearer</c>); scope <c>octo_api</c>, roles from a header.</summary>
    private sealed class TestBearerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Bearer";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim>
            {
                new("sub", "65f0c0ffee65f0c0ffee9056"), new("scope", CommonConstants.OctoApiFullAccess),
                new("tenant_id", Request.Headers["X-Tenant"].ToString())
            };
            claims.AddRange(Request.Headers["X-Roles"].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim("role", r)));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer", "name", "role")), SchemeName)));
        }
    }
}
