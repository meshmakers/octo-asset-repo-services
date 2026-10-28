using System.Security.Claims;
using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Xunit;
using static Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures.GatewayOwnedMethodGraphQlTestFixture;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.CkV2;

[CollectionDefinition(Name)]
public class GatewayOwnedMethodCollection : ICollectionFixture<GatewayOwnedMethodGraphQlTestFixture>
{
    public const string Name = "GatewayOwnedMethod";
}

/// <summary>
///     AB#6893 (F3.7): a method declared <c>handler: service</c> that the gateway owns runs as in-process handler of the
///     asset repository. Real host (see <see cref="GatewayOwnedMethodGraphQlTestFixture" />): a GraphQL mutation goes
///     through the gateway and the dispatcher into the in-process transport, and
///     <list type="bullet">
///         <item>nothing is published to the bus and nothing is announced as remote handler;</item>
///         <item>the answer for success, declared error, exception and timeout equals the answer of the bus path (the
///         remote handler host of the same handler class);</item>
///         <item>the in-process path is no bypass: the declared authorization is enforced by the host, with and without
///         the gateway, and a call without a principal is refused.</item>
///     </list>
/// </summary>
[Collection(GatewayOwnedMethodCollection.Name)]
public class GatewayOwnedMethodInProcessTests
{
    private const string Field = "assetRepositoryIntegrationTestAccessTestAccounts";
    private const string CkTypeId = "AssetRepositoryIntegrationTest/AccessTestAccount";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private readonly GatewayOwnedMethodGraphQlTestFixture _fixture;

    public GatewayOwnedMethodInProcessTests(GatewayOwnedMethodGraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
        _fixture.Observation.Reset();
    }

    private static ClaimsPrincipal User(string sub, params string[] roles)
    {
        var claims = new List<Claim> { new("sub", sub), new("scope", "octo_api openid") };
        claims.AddRange(roles.Select(r => new Claim("role", r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static ClaimsPrincipal Admin() => User("65f0c0ffee65f0c0ffee65f1", "AccessTestAdmin");

    private async Task<string> CreateAccountAsync()
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Field}} { create(entities: [{ name: "acct-{{Guid.NewGuid():N}}" }]) { rtId } } } }
            """);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return JObject.Parse(json).SelectToken($"data.runtime.{Field}.create[0].rtId")!.Value<string>()!;
    }

    private Task<ExecutionResult> ResetPasswordAsync(string rtId, string newPassword, ClaimsPrincipal? user) =>
        _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Field}} {
              resetPassword(rtId: "{{rtId}}", input: { newPassword: "{{newPassword}}" }) { invocationId }
            } } }
            """, user: user);

    private static string ErrorCode(ExecutionResult result) =>
        result.Errors!.Should().ContainSingle().Subject.Code!;

    [Fact]
    public void TheMethodIsRegisteredInProcess_AndNothingIsAnnouncedAsRemoteHandler()
    {
        var transport = _fixture.GetService<ICkInProcessMethodTransport>();

        transport.MethodIds.Should().Contain(MethodId);
        transport.CanHandle(MethodId).Should().BeTrue();

        // the remote host (announcement at start, heartbeat, bus consumer queue) is not part of this host
        _fixture.RegisteredServices
            .Select(d => d.ImplementationType?.Name)
            .Should().NotContain(["CkMethodHandlerStartupService", "CkMethodHandlerHeartbeatService",
                "CkMethodHandlerValidationService"]);
        A.CallTo(_fixture.Bus).MustNotHaveHappened();
    }

    [Fact]
    public async Task Success_RunsTheHandlerInProcess_WithoutSession_AndPublishesNothing()
    {
        var rtId = await CreateAccountAsync();

        var result = await ResetPasswordAsync(rtId, "correct-horse", Admin());

        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        JObject.Parse(json).SelectToken($"data.runtime.{Field}.resetPassword.invocationId")!.Value<string>()
            .Should().NotBeNullOrEmpty();
        _fixture.Observation.Runs.Should().Be(1);
        _fixture.Observation.LastTargetRtId.Should().Be(rtId);
        _fixture.Observation.LastCallerSubject.Should().Be("65f0c0ffee65f0c0ffee65f1");
        _fixture.Observation.HasSessionInHandler.Should().BeFalse("methods run outside the request transaction");
        A.CallTo(_fixture.Bus).MustNotHaveHappened();
    }

    [Fact]
    public async Task DeclaredError_ReachesTheCaller_AndPublishesNothing()
    {
        var rtId = await CreateAccountAsync();

        var result = await ResetPasswordAsync(rtId, "weak", Admin());

        ErrorCode(result).Should().Be(DeclaredError);
        _fixture.Observation.Runs.Should().Be(1);
        A.CallTo(_fixture.Bus).MustNotHaveHappened();
    }

    [Fact]
    public async Task HandlerException_IsMethodHandlerFailed_WithoutTheExceptionText()
    {
        var rtId = await CreateAccountAsync();

        var result = await ResetPasswordAsync(rtId, "boom", Admin());

        ErrorCode(result).Should().Be(CkMethodErrorCodes.MethodHandlerFailed);
        _fixture.SerializeGraphQl(result).Should().NotContain("sample handler failure");
        A.CallTo(_fixture.Bus).MustNotHaveHappened();
    }

    [Fact]
    public async Task HandlerRunningUntilItsDeadline_IsMethodTimeout()
    {
        var rtId = await CreateAccountAsync();

        var result = await ResetPasswordAsync(rtId, "hang", Admin());

        ErrorCode(result).Should().Be(CkMethodErrorCodes.MethodTimeout);
        A.CallTo(_fixture.Bus).MustNotHaveHappened();
    }

    [Fact]
    public async Task CallerWithoutTheDeclaredRole_IsForbidden_AndTheHandlerDoesNotRun()
    {
        var rtId = await CreateAccountAsync();

        var result = await ResetPasswordAsync(rtId, "correct-horse", User("65f0c0ffee65f0c0ffee65f2", "Nobody"));

        ErrorCode(result).Should().Be(CkMethodErrorCodes.MethodForbidden);
        _fixture.Observation.Runs.Should().Be(0);
        A.CallTo(_fixture.Bus).MustNotHaveHappened();
    }

    // The in-process transport is no bypass of the authorization: a caller that does not come through the gateway has no
    // principal and is refused by the host, one with a principal that lacks the declared role is refused as well.

    [Fact]
    public async Task TransportCalledWithoutAPrincipal_IsForbidden_AndTheHandlerDoesNotRun()
    {
        var response = await _fixture.GetService<ICkInProcessMethodTransport>()
            .InvokeAsync(Request(_fixture.SystemTenantId, "correct-horse"), TestContext.Current.CancellationToken);

        response.Success.Should().BeFalse();
        response.Error!.Code.Should().Be(CkMethodErrorCodes.MethodForbidden);
        _fixture.Observation.Runs.Should().Be(0);
    }

    [Fact]
    public async Task TransportCalledWithAPrincipalWithoutTheDeclaredRole_IsForbidden_AndTheHandlerDoesNotRun()
    {
        using var scope = CkMethodPrincipalScope.Begin(new CkMethodPrincipal("sub-x", ["Nobody"], ["octo_api"]));

        var response = await _fixture.GetService<ICkInProcessMethodTransport>()
            .InvokeAsync(Request(_fixture.SystemTenantId, "correct-horse"), TestContext.Current.CancellationToken);

        response.Error!.Code.Should().Be(CkMethodErrorCodes.MethodForbidden);
        _fixture.Observation.Runs.Should().Be(0);
    }

    // The envelope equals the one of the bus path. The bus path is the remote handler host of the same handler class (the
    // consumer behind the queue is a thin shell around exactly this invoker); the bus itself is not needed to compare them.

    [Fact]
    public Task Success_EnvelopeEqualsTheBusPath() => AssertSameEnvelopeAsync("correct-horse", null);

    [Fact]
    public async Task DeclaredError_EnvelopeEqualsTheBusPath()
    {
        var local = await AssertSameEnvelopeAsync("weak", null);
        local.Error!.Code.Should().Be(DeclaredError);
    }

    [Fact]
    public async Task HandlerException_EnvelopeEqualsTheBusPath()
    {
        var local = await AssertSameEnvelopeAsync("boom", null);
        local.Error!.Code.Should().Be(CkMethodErrorCodes.MethodHandlerFailed);
    }

    [Fact]
    public async Task Timeout_EnvelopeEqualsTheBusPath()
    {
        var local = await AssertSameEnvelopeAsync("hang", 1);
        local.Error!.Code.Should().Be(CkMethodErrorCodes.MethodTimeout);
    }

    private static CkMethodRequest Request(string tenant, string newPassword, int timeoutSeconds = 30) => new(tenant)
    {
        InvocationId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        MethodId = MethodId,
        CkTypeId = CkTypeId,
        TargetRtId = "65f0c0ffee65f0c0ffee65f3",
        ParametersJson = JsonSerializer.Serialize(new { newPassword }),
        TimeoutSeconds = timeoutSeconds,
        CallerAccessToken = "token",
        Caller = new CkMethodCaller { SubjectId = "sub-1" },
        DeadlineUtc = DateTime.UtcNow.AddSeconds(timeoutSeconds)
    };

    private async Task<CkMethodResponse> AssertSameEnvelopeAsync(string newPassword, int? timeoutSeconds)
    {
        var tenant = _fixture.SystemTenantId;
        var seconds = timeoutSeconds ?? 30;
        var local = Request(tenant, newPassword, seconds);

        CkMethodResponse inProcess;
        using (CkMethodPrincipalScope.Begin(new CkMethodPrincipal("sub-1", ["AccessTestAdmin"], ["octo_api"])))
        {
            inProcess = await _fixture.GetService<ICkInProcessMethodTransport>()
                .InvokeAsync(local, TestContext.Current.CancellationToken);
        }

        using var remoteHost = BuildRemoteHost(tenant);
        var viaBus = await remoteHost.GetRequiredService<ICkMethodHandlerInvoker>()
            .InvokeAsync(local with { DeadlineUtc = DateTime.UtcNow.AddSeconds(seconds) },
                TestContext.Current.CancellationToken);

        Normalized(inProcess).Should().Be(Normalized(viaBus));
        _fixture.Observation.Runs.Should().Be(2, "once per path");
        A.CallTo(_fixture.Bus).MustNotHaveHappened();
        return inProcess;
    }

    private static string Normalized(CkMethodResponse response) =>
        JsonSerializer.Serialize(response with { HandlerInstanceId = "<instance>" }, Wire);

    /// <summary>
    ///     The handler host a remote service would run for this method (<c>AddCkMethodHandlers</c>, the same handler class and
    ///     the CK cache of this host), without a bus. It is what the bus consumer calls. The host would validate the caller
    ///     token (<c>AddCkMethodCallerTokenValidation</c>); here the authorization is left open because the roles of the
    ///     caller are proven by the tests above, not by this envelope comparison.
    /// </summary>
    private ServiceProvider BuildRemoteHost(string tenant)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton(_fixture.GetService<ICkCacheService>());
        services.AddSingleton(_fixture.GetService<SampleObservation>());
        services.AddSingleton(_fixture.GetService<Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling.IOctoSessionAccessor>());
        services.AddSingleton<IOptions<OctoSystemConfiguration>>(
            Options.Create(new OctoSystemConfiguration { SystemTenantId = tenant }));
        services.AddSingleton<ICkMethodAuthorizationEvaluator, AllowAnyCaller>();
        services.AddScoped<SampleResetPasswordHandler>();
        services.AddCkMethodHandlers("asset-repo-test-remote", m => m.Handle<SampleResetPasswordHandler>(MethodId));
        services.Configure<CkMethodHandlerOptions>(o => o.ValidationTenantIds = [tenant]);
        var provider = services.BuildServiceProvider();
        // the validation step of the host start: fills the definition of the registration
        var descriptor = services.Single(d => d.ImplementationType?.Name == "CkMethodHandlerValidationService");
        var validation = (Meshmakers.Octo.Services.Infrastructure.Initialization.IAsyncInitializationService)
            ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
        validation.InitializeAsync().GetAwaiter().GetResult();
        return provider;
    }

    private sealed class AllowAnyCaller : ICkMethodAuthorizationEvaluator
    {
        public CkMethodAuthorizationDecision Evaluate(CkMethodAuthorizationDto? declaration, CkMethodKindDto kind,
            string? targetRtId, CkMethodPrincipal? principal) => CkMethodAuthorizationDecision.Allow;
    }
}
