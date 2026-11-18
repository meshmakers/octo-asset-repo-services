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
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
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
///     administrator through to the direct call. Only the authentication handler is a stand-in. The column routes
///     are checked on the method path as well.
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

    /// <summary>
    ///     the six column routes through the real pipeline, on the method path (the host's real facade) and on the
    ///     fallback. A caller without <c>StreamDataAdmin</c> is refused by the REST policy with <c>403</c>, an administrator token
    ///     of another tenant with <c>403 METHOD_FORBIDDEN</c>; neither changes the archive or queues a job. The administrator
    ///     reaches the route (the positive control: a <c>403</c> is no unmatched route).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheColumnRoutes_RefuseACallerWithoutTheRole_AndAForeignToken_403_AndNothingChanges(bool methodPath)
    {
        var archive = await CreateArchiveWithComputedColumnAsync("kept");
        var before = (await SnapshotAsync(archive))!.Columns;
        var jobsBefore = await JobCountAsync(archive);
        var facade = methodPath ? RealFacade() : SwitchedOffFacade();
        await using var host = await StartAsync(facade);
        var basePath = $"/{fixture.GetSystemContext().TenantId}/v1/streamdata/archives/{archive}";
        var routes = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Post, $"{basePath}/computed-columns?name=x&formula=Voltage&resultType=Double"),
            (HttpMethod.Post, $"{basePath}/columns?path=Current"),
            (HttpMethod.Delete, $"{basePath}/computed-columns/kept"),
            (HttpMethod.Put, $"{basePath}/computed-columns/kept?formula=Voltage%20*%209"),
            (HttpMethod.Post, $"{basePath}/computed-columns/kept/retry"),
            (HttpMethod.Post, $"{basePath}/computed-columns/kept/repair")
        };

        foreach (var (method, path) in routes)
        {
            var withoutRole = await host.SendAsync(method, path, roles: "SomeOtherRole");
            withoutRole.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} {path} without the role");

            var foreign = await host.SendAsync(method, path, roles: CommonConstants.StreamDataAdminRole,
                tokenTenant: "some-other-tenant");
            foreign.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} {path} with a foreign token");
            (await foreign.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should()
                .Contain(CkMethodErrorCodes.MethodForbidden, $"{method} {path}");
        }

        (await SnapshotAsync(archive))!.Columns.Should().BeEquivalentTo(before, "no refused call changed the archive");
        (await JobCountAsync(archive)).Should().Be(jobsBefore, "no refused call queued a job");

        var allowed = await host.SendAsync(HttpMethod.Post, $"{basePath}/columns?path=Current",
            roles: CommonConstants.StreamDataAdminRole);
        allowed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SnapshotAsync(archive))!.Columns.Should().Contain(c => c.Path == "Current");
        if (!methodPath)
        {
            A.CallTo(facade).Where(call => call.Method.Name == nameof(IArchiveMethodFacade.InvokeAsync)).MustNotHaveHappened();
        }
    }

    /// <summary>The host's real facade (switch on, the tenant's model declares the methods).</summary>
    private IArchiveMethodFacade RealFacade() =>
        fixture.GetService<IServiceScopeFactory>().CreateScope().ServiceProvider.GetRequiredService<IArchiveMethodFacade>();

    private static IArchiveMethodFacade SwitchedOffFacade()
    {
        var facade = A.Fake<IArchiveMethodFacade>();
        A.CallTo(() => facade.IsHosted).Returns(false);
        A.CallTo(() => facade.CanServe(A<ITenantContext>._, A<string>._, A<string>._)).Returns(false);
        return facade;
    }

    private async Task<int> JobCountAsync(OctoObjectId archive)
    {
        var systemContext = fixture.GetSystemContext();
        var tenant = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        return (await tenant.GetRecomputeJobStore()!.GetForArchiveAsync(archive, 1_000)).Count;
    }

    /// <summary>An activated raw archive with an Active computed column.</summary>
    private async Task<OctoObjectId> CreateArchiveWithComputedColumnAsync(string column)
    {
        var archive = await CreateRawArchiveAsync();
        var systemContext = fixture.GetSystemContext();
        var lifecycle = (await systemContext.FindTenantContextAsync(systemContext.TenantId)).GetArchiveLifecycleService()!;
        await lifecycle.ActivateAsync(archive);
        await lifecycle.EnqueueAddComputedColumnAsync(archive, column, "Voltage * 2", FormulaResultType.Double, indexed: false);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await lifecycle.RunPendingComputedColumnBackfillsAsync(TestContext.Current.CancellationToken);
            if ((await SnapshotAsync(archive))!.Columns.Single(c => c.Name == column).ComputedState == ComputedColumnState.Active)
            {
                return archive;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException("the backfill drain did not settle the computed column");
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

        public Task<HttpResponseMessage> PostAsync(string path, string roles, string? tokenTenant = null) =>
            SendAsync(HttpMethod.Post, path, roles, tokenTenant);

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string roles, string? tokenTenant = null)
        {
            var request = new HttpRequestMessage(method, path);
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
