using System.Security.Claims;
using FakeItEasy;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Audit;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
///     The eight REST lifecycle and rollup endpoints of <see cref="StreamDataController" /> and the
///     deprecated <c>streamData</c> mutations are facades of the Archive methods. Per endpoint: the method's transition with
///     the REST answer (<c>204</c>) and exactly one Method audit event; the handler runs without the request session; a
///     transition the status does not allow answers the same code on the mutation and the method and the same message on
///     REST; a token of another tenant and a caller without <c>StreamDataAdmin</c> are refused and change nothing; with
///     <c>Methods:StreamData:Enabled=false</c> the endpoints and the mutations keep their direct lifecycle call.
/// </summary>
[Collection(ArchiveMethodFacadeCollection.Name)]
public class ArchiveMethodFacadeTests
{
    private static readonly DateTime Until = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly ArchiveMethodFacadeFixture _fixture;

    public ArchiveMethodFacadeTests(ArchiveMethodFacadeFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    // ── the eight endpoints: method parity, 204, one audit event each ──────────────────────────

    [Fact]
    public async Task EveryEndpoint_RunsItsMethod_Answers204_WithExactlyOneAuditEvent()
    {
        var created = await CreateRawArchiveAsync("RestAct");
        var activated = await CreateRawArchiveAsync("RestDis", activate: true);
        var failed = await CreateRawArchiveAsync("RestRetry");
        await (await TenantAsync()).GetArchiveRuntimeStore().SetStatusAsync(failed, CkArchiveStatus.Failed);
        var deleted = await CreateRawArchiveAsync("RestDel", activate: true);
        var rollup = await CreateRollupAsync("RestRollup");

        await ExpectOneEventAsync(SystemStreamDataCkIds.ArchiveActivateMethodId, c => c.ActivateArchive(Tenant, $"{created}"));
        (await SnapshotAsync(created))!.Status.Should().Be(CkArchiveStatus.Activated);

        await ExpectOneEventAsync(SystemStreamDataCkIds.ArchiveDisableMethodId, c => c.DisableArchive(Tenant, $"{activated}"));
        (await SnapshotAsync(activated))!.Status.Should().Be(CkArchiveStatus.Disabled);

        await ExpectOneEventAsync(SystemStreamDataCkIds.ArchiveEnableMethodId, c => c.EnableArchive(Tenant, $"{activated}"));
        (await SnapshotAsync(activated))!.Status.Should().Be(CkArchiveStatus.Activated);

        await ExpectOneEventAsync(SystemStreamDataCkIds.ArchiveRetryActivationMethodId,
            c => c.RetryArchiveActivation(Tenant, $"{failed}"));
        (await SnapshotAsync(failed))!.Status.Should().Be(CkArchiveStatus.Activated);

        await ExpectOneEventAsync(SystemStreamDataCkIds.ArchiveDeleteArchiveMethodId, c => c.DeleteArchive(Tenant, $"{deleted}"));
        (await SnapshotAsync(deleted)).Should().BeNull("DeleteArchive-1 deletes the entity");

        await ExpectOneEventAsync(SystemStreamDataCkIds.RollupArchiveFreezeMethodId,
            c => c.FreezeRollupArchive(Tenant, $"{rollup}", Until));
        (await RollupAsync(rollup)).FrozenUntil.Should().Be(Until);

        await ExpectOneEventAsync(SystemStreamDataCkIds.RollupArchiveUnfreezeMethodId,
            c => c.UnfreezeRollupArchive(Tenant, $"{rollup}", acceptGaps: true));
        (await RollupAsync(rollup)).FrozenUntil.Should().BeNull();

        // inside the bucket: the method aligns it down to the bucket boundary, as the direct call did
        await ExpectOneEventAsync(SystemStreamDataCkIds.RollupArchiveRewindWatermarkMethodId,
            c => c.RewindRollupWatermark(Tenant, $"{rollup}", Until.AddMinutes(17)));
        (await RollupAsync(rollup)).LastAggregatedBucketEnd.Should().Be(Until);
    }

    [Fact]
    public async Task EveryMutation_RunsItsMethod_AndAnswersTheMutationsResult_WithExactlyOneAuditEvent()
    {
        var archive = await CreateRawArchiveAsync("MutAll");
        var failed = await CreateRawArchiveAsync("MutRetry");
        await (await TenantAsync()).GetArchiveRuntimeStore().SetStatusAsync(failed, CkArchiveStatus.Failed);
        var rollup = await CreateRollupAsync("MutRollup");
        var calls = new (string MethodId, string Mutation, OctoObjectId RtId, string Transition, string Status)[]
        {
            (SystemStreamDataCkIds.ArchiveActivateMethodId, $$"""activateArchive(rtId: "{{archive}}")""", archive, "Activate", "ACTIVATED"),
            (SystemStreamDataCkIds.ArchiveDisableMethodId, $$"""disableArchive(rtId: "{{archive}}")""", archive, "Disable", "DISABLED"),
            (SystemStreamDataCkIds.ArchiveEnableMethodId, $$"""enableArchive(rtId: "{{archive}}")""", archive, "Enable", "ACTIVATED"),
            (SystemStreamDataCkIds.ArchiveRetryActivationMethodId, $$"""retryArchiveActivation(rtId: "{{failed}}")""", failed,
                "RetryActivation", "ACTIVATED"),
            (SystemStreamDataCkIds.RollupArchiveFreezeMethodId, $$"""freezeRollupArchive(rtId: "{{rollup}}", until: "{{Until:O}}")""",
                rollup, "Freeze", "CREATED"),
            (SystemStreamDataCkIds.RollupArchiveUnfreezeMethodId, $$"""unfreezeRollupArchive(rtId: "{{rollup}}", acceptGaps: true)""",
                rollup, "Unfreeze", "CREATED"),
            (SystemStreamDataCkIds.RollupArchiveRewindWatermarkMethodId,
                $$"""rewindRollupWatermark(rtId: "{{rollup}}", toBucketEnd: "{{Until:O}}")""", rollup, "RewindWatermark", "CREATED")
        };

        foreach (var (methodId, mutation, rtId, transition, status) in calls)
        {
            _fixture.AuditEvents.Clear();
            var result = await _fixture.ExecuteGraphQlAsync(
                $"mutation {{ streamData {{ {mutation} {{ archiveRtId status transition }} }} }}", user: Admin());

            var json = _fixture.SerializeGraphQl(result);
            result.Errors.Should().BeNullOrEmpty(json);
            var node = JObject.Parse(json).SelectToken("data.streamData")!.First!.First!;
            node.Value<string>("archiveRtId").Should().Be(rtId.ToString(), methodId);
            node.Value<string>("transition").Should().Be(transition, methodId);
            node.Value<string>("status")!.Replace("_", "", StringComparison.Ordinal).ToUpperInvariant()
                .Should().Be(status, methodId);
            _fixture.AuditEvents.Should().ContainSingle(methodId).Which.MethodId.Should().Be(methodId);
        }

        _fixture.AuditEvents.Clear();
        var delete = await _fixture.ExecuteGraphQlAsync(
            $$"""mutation { streamData { deleteArchive(rtId: "{{archive}}") } }""", user: Admin());
        JObject.Parse(_fixture.SerializeGraphQl(delete)).SelectToken("data.streamData.deleteArchive")!.Value<bool>()
            .Should().BeTrue();
        _fixture.AuditEvents.Should().ContainSingle().Which.MethodId.Should().Be(SystemStreamDataCkIds.ArchiveDeleteArchiveMethodId);
        (await SnapshotAsync(archive)).Should().BeNull();
        (await RollupAsync(rollup)).LastAggregatedBucketEnd.Should().Be(Until);
    }

    [Fact]
    public async Task TheHandler_RunsOutsideTheRequestSession_EvenWhenTheCallerHoldsOne()
    {
        var archive = await CreateRawArchiveAsync("RestSession");
        using var scope = _fixture.GetService<IServiceScopeFactory>().CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IOctoSessionAccessor>();
        var repository = _fixture.GetSystemContext().GetSystemTenantRepository();
        using var requestSession = await repository.GetSessionAsync();
        requestSession.StartTransaction();
        accessor.Session = requestSession;
        try
        {
            _fixture.SessionVisibleAtHandlerEntry.Clear();
            accessor.HasSession.Should().BeTrue("the caller runs inside a request transaction");

            var answer = await Controller(Admin(), scope.ServiceProvider).ActivateArchive(Tenant, $"{archive}");

            answer.Should().BeOfType<NoContentResult>();
            _fixture.SessionVisibleAtHandlerEntry.Should().ContainSingle().Which.Should()
                .BeFalse("the in-process handler starts without the request session");
            accessor.HasSession.Should().BeTrue("the request session is untouched");
            (await SnapshotAsync(archive))!.Status.Should().Be(CkArchiveStatus.Activated,
                "the method committed on its own, not in the request transaction");

            // the deprecated mutation, the second caller of the facade, inside the same request transaction
            _fixture.SessionVisibleAtHandlerEntry.Clear();
            var mutation = await _fixture.GetService<IDocumentExecuter<OctoSchema>>().ExecuteAsync(options =>
            {
                options.Schema = null;
                options.Query = $$"""mutation { streamData { disableArchive(rtId: "{{archive}}") { archiveRtId status transition } } }""";
                options.RequestServices = scope.ServiceProvider;
                options.UserContext = new GraphQlUserContext(Admin(), TenantAsync().GetAwaiter().GetResult());
            });

            mutation.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(mutation));
            _fixture.SessionVisibleAtHandlerEntry.Should().ContainSingle().Which.Should()
                .BeFalse("the mutation's handler starts without the request session too");
            accessor.HasSession.Should().BeTrue("the request session is untouched");
            (await SnapshotAsync(archive))!.Status.Should().Be(CkArchiveStatus.Disabled);
        }
        finally
        {
            await accessor.AbortAsync();
        }
    }

    // ── refusals: same code on all three paths, abuse cases ─────────────────────────────────────

    [Fact]
    public async Task ATransitionTheStatusDoesNotAllow_AnswersTheSameCodeOnTheMutationAndTheMethod_AndTheSameMessageOnRest()
    {
        var archive = await CreateRawArchiveAsync("RestInvalid");

        var rest = await Controller(Admin()).DisableArchive(Tenant, $"{archive}");
        var mutation = await _fixture.ExecuteGraphQlAsync(
            $$"""mutation { streamData { disableArchive(rtId: "{{archive}}") { archiveRtId status transition } } }""",
            user: Admin());
        var method = await _fixture.ExecuteGraphQlAsync(
            $$"""mutation { runtime { systemStreamDataRawArchives { disable(rtId: "{{archive}}") { invocationId } } } }""",
            user: Admin());

        var methodError = method.Errors.Should().ContainSingle().Subject;
        methodError.Code.Should().Be(Statics.GraphQlErrorStreamDataInvalidTransition);
        var mutationError = mutation.Errors!.First();
        mutationError.Code.Should().Be(methodError.Code, "the mutation answers the method's code");
        var message = JObject.Parse(_fixture.SerializeGraphQl(method))
            .SelectToken("errors[0].extensions.parameters.message")!.Value<string>();
        mutationError.Message.Should().Be(message);
        mutationError.Extensions!["fromStatus"].Should().Be("Created");
        mutationError.Extensions["archiveRtId"].Should().Be(archive.ToString());

        var badRequest = rest.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value.Should().Be(message, "REST keeps its 400 with the engine message as the body");
        (await SnapshotAsync(archive))!.Status.Should().Be(CkArchiveStatus.Created);
    }

    [Fact]
    public async Task AMissingArchive_StaysA400WithTheNotFoundMessage_OnRest_AndTheNotFoundCodeOnTheMutation()
    {
        var missing = OctoObjectId.GenerateNewId();

        var rest = await Controller(Admin()).ActivateArchive(Tenant, $"{missing}");
        var mutation = await _fixture.ExecuteGraphQlAsync(
            $$"""mutation { streamData { activateArchive(rtId: "{{missing}}") { archiveRtId } } }""", user: Admin());

        rest.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should()
            .Be(new ArchiveNotFoundException(missing).Message);
        var error = mutation.Errors!.First();
        error.Code.Should().Be(Statics.GraphQlErrorStreamDataArchiveNotFound);
        error.Extensions!["archiveRtId"].Should().Be(missing.ToString());
    }

    [Fact]
    public async Task ATokenOfAnotherTenant_IsRefusedOnEveryEndpoint_403TenantNotAllowed_AndNothingChanges()
    {
        var foreignAdmin = UserOf("some-other-tenant", CommonConstants.StreamDataAdminRole);
        await AssertEveryEndpointRefusedAsync(foreignAdmin, CkMethodTokenReasons.TenantNotAllowed);
    }

    [Fact]
    public async Task ACallerWithoutStreamDataAdmin_IsRefusedOnEveryEndpoint_403_AndNothingChanges()
    {
        // the REST policy refuses this caller before the action; the method's declared role is the second line
        await AssertEveryEndpointRefusedAsync(User("SomeOtherRole"), expectedReason: null);
    }

    [Fact]
    public async Task ATokenOfAnotherTenant_IsRefusedOnTheDeprecatedMutations_AndNothingChanges()
    {
        var archive = await CreateRawArchiveAsync("MutForeign");
        var rollup = await CreateRollupAsync("MutForeignRollup");

        var activate = await _fixture.ExecuteGraphQlAsync(
            $$"""mutation { streamData { activateArchive(rtId: "{{archive}}") { archiveRtId } } }""",
            user: UserOf("some-other-tenant", CommonConstants.StreamDataAdminRole));
        var freeze = await _fixture.ExecuteGraphQlAsync(
            $$"""mutation { streamData { freezeRollupArchive(rtId: "{{rollup}}", until: "{{Until:O}}") { archiveRtId } } }""",
            user: UserOf("some-other-tenant", CommonConstants.StreamDataAdminRole));

        foreach (var result in new[] { activate, freeze })
        {
            result.Errors!.First().Code.Should().Be(CkMethodErrorCodes.MethodForbidden);
            JObject.Parse(_fixture.SerializeGraphQl(result)).SelectToken("errors[0].extensions.parameters.reason")!
                .Value<string>().Should().Be(CkMethodTokenReasons.TenantNotAllowed);
        }

        (await SnapshotAsync(archive))!.Status.Should().Be(CkArchiveStatus.Created);
        (await RollupAsync(rollup)).FrozenUntil.Should().BeNull();
    }

    // ── Methods:StreamData:Enabled=false ────────────────────────────────────────────────────────

    [Fact]
    public async Task WithTheSwitchOff_RestAndTheMutations_KeepTheirDirectLifecycleCall()
    {
        var viaRest = await CreateRawArchiveAsync("OffRest");
        var viaMutation = await CreateRawArchiveAsync("OffMut");
        var rollup = await CreateRollupAsync("OffRollup");
        var notHosted = A.Fake<IArchiveMethodFacade>();
        A.CallTo(() => notHosted.IsHosted).Returns(false);
        A.CallTo(() => notHosted.CanServe(A<ITenantContext>._, A<string>._, A<string>._)).Returns(false);
        using var scope = _fixture.GetService<IServiceScopeFactory>().CreateScope();
        var services = new OverridingServiceProvider(scope.ServiceProvider, notHosted);
        _fixture.AuditEvents.Clear();

        (await Controller(Admin(), services).ActivateArchive(Tenant, $"{viaRest}")).Should().BeOfType<NoContentResult>();
        (await Controller(Admin(), services).FreezeRollupArchive(Tenant, $"{rollup}", Until))
            .Should().BeOfType<NoContentResult>();
        var mutation = await _fixture.GetService<IDocumentExecuter<OctoSchema>>().ExecuteAsync(options =>
        {
            options.Schema = null;
            options.Query = $$"""mutation { streamData { activateArchive(rtId: "{{viaMutation}}") { archiveRtId status transition } } }""";
            options.RequestServices = services;
            options.UserContext = new GraphQlUserContext(Admin(), TenantAsync().GetAwaiter().GetResult());
        });

        mutation.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(mutation));
        (await SnapshotAsync(viaRest))!.Status.Should().Be(CkArchiveStatus.Activated);
        (await SnapshotAsync(viaMutation))!.Status.Should().Be(CkArchiveStatus.Activated);
        (await RollupAsync(rollup)).FrozenUntil.Should().Be(Until);
        A.CallTo(notHosted).Where(call => call.Method.Name == nameof(IArchiveMethodFacade.InvokeAsync))
            .MustNotHaveHappened();
        // no method ran; every direct call wrote exactly one event of the fallback guard
        _fixture.AuditEvents.Should().HaveCount(3).And.OnlyContain(e =>
            e.Transport == ArchiveMethodFallbackGuard.Transport && e.Outcome == CkMethodAuditOutcomes.Success);
    }

    [Fact]
    public async Task ATenantWhoseCkModelDoesNotDeclareTheMethod_KeepsTheDirectCall_NotA403()
    {
        // A tenant whose System.StreamData is older than the methods or not resolved: the gateway would refuse the call as an
        // unknown method (403), where the endpoint and the mutation worked before. The facade sees the missing declaration
        // and keeps the direct lifecycle call. The real facade, with a definition source that knows no method.
        var viaRest = await CreateRawArchiveAsync("UnknownRest");
        var viaMutation = await CreateRawArchiveAsync("UnknownMut");
        var rollup = await CreateRollupAsync("UnknownRollup");
        using var scope = _fixture.GetService<IServiceScopeFactory>().CreateScope();
        var noDefinitions = A.Fake<ICkMethodAuthorizationDefinitionSource>();
        A.CallTo(() => noDefinitions.Find(A<string>._, A<string>._, A<string>._)).Returns(null);
        var facade = new ArchiveMethodFacade(scope.ServiceProvider.GetRequiredService<ICkMethodGateway>(),
            scope.ServiceProvider.GetRequiredService<IRtDataPermissionEvaluator>(), noDefinitions,
            scope.ServiceProvider.GetRequiredService<IOptions<StreamDataMethodsOptions>>());
        facade.IsHosted.Should().BeTrue("the switch is on; only the tenant's model lacks the method");
        facade.CanServe(await TenantAsync(), ArchiveMethodFacade.ArchiveCkTypeId,
            SystemStreamDataCkIds.ArchiveActivateMethodId).Should().BeFalse();
        var services = new OverridingServiceProvider(scope.ServiceProvider, facade);
        _fixture.AuditEvents.Clear();

        (await Controller(Admin(), services).ActivateArchive(Tenant, $"{viaRest}")).Should().BeOfType<NoContentResult>();
        (await Controller(Admin(), services).FreezeRollupArchive(Tenant, $"{rollup}", Until))
            .Should().BeOfType<NoContentResult>();
        var mutation = await _fixture.GetService<IDocumentExecuter<OctoSchema>>().ExecuteAsync(options =>
        {
            options.Schema = null;
            options.Query = $$"""mutation { streamData { activateArchive(rtId: "{{viaMutation}}") { archiveRtId status transition } } }""";
            options.RequestServices = services;
            options.UserContext = new GraphQlUserContext(Admin(), TenantAsync().GetAwaiter().GetResult());
        });

        mutation.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(mutation));
        (await SnapshotAsync(viaRest))!.Status.Should().Be(CkArchiveStatus.Activated);
        (await SnapshotAsync(viaMutation))!.Status.Should().Be(CkArchiveStatus.Activated);
        (await RollupAsync(rollup)).FrozenUntil.Should().Be(Until);
        // no method ran; every direct call wrote exactly one event of the fallback guard
        _fixture.AuditEvents.Should().HaveCount(3).And.OnlyContain(e =>
            e.Transport == ArchiveMethodFallbackGuard.Transport && e.Outcome == CkMethodAuditOutcomes.Success);

        // the real definition source of this host knows the methods: the facade serves them
        _fixture.GetService<IArchiveMethodFacade>().CanServe(await TenantAsync(), ArchiveMethodFacade.RollupArchiveCkTypeId,
            SystemStreamDataCkIds.RollupArchiveFreezeMethodId).Should().BeTrue();
    }

    [Fact]
    public void TheFacade_IsRegisteredWithTheSwitchOnAndOff_AndFollowsIt()
    {
        foreach (var (value, hosted) in new[] { ((string?)null, true), ("true", true), ("false", false) })
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(value == null ? [] : [new KeyValuePair<string, string?>("Methods:StreamData:Enabled", value)])
                .Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);

            services.AddArchiveCkMethodHandlers(configuration).Should().Be(hosted);
            services.Should().ContainSingle(d => d.ServiceType == typeof(IArchiveMethodFacade));
            ArchiveMethodHost.ReadOptions(configuration).Enabled.Should().Be(hosted);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private string Tenant => _fixture.GetSystemContext().TenantId;

    private ClaimsPrincipal User(params string[] roles) => UserOf(Tenant, roles);

    private ClaimsPrincipal Admin() => User(CommonConstants.StreamDataAdminRole);

    private static ClaimsPrincipal UserOf(string tokenTenant, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("sub", "65f0c0ffee65f0c0ffee9055"), new("scope", "octo_api openid"), new(ClaimTypes.Name, "archive-admin"),
            new("tenant_id", tokenTenant)
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim("role", role));
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private StreamDataController Controller(ClaimsPrincipal user, IServiceProvider? services = null)
    {
        var controller = new StreamDataController(NullLogger<StreamDataController>.Instance, _fixture.GetSystemContext(),
            A.Fake<IHostApplicationLifetime>());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = user,
                RequestServices = services ?? _fixture.GetService<IServiceScopeFactory>().CreateScope().ServiceProvider
            }
        };
        return controller;
    }

    private async Task ExpectOneEventAsync(string methodId, Func<StreamDataController, Task<IActionResult>> call)
    {
        _fixture.AuditEvents.Clear();

        var answer = await call(Controller(Admin()));

        answer.Should().BeOfType<NoContentResult>(methodId);
        var events = _fixture.AuditEvents.ToList();
        events.Should().ContainSingle(methodId).Which.Should().Match<CkMethodAuditRecord>(e =>
            e.MethodId == methodId && e.Outcome == CkMethodAuditOutcomes.Success && e.TenantId == Tenant);
    }

    /// <summary>Every endpoint refused with 403 METHOD_FORBIDDEN (and the reason), one audit event each, nothing changed.</summary>
    private async Task AssertEveryEndpointRefusedAsync(ClaimsPrincipal caller, string? expectedReason)
    {
        var created = await CreateRawArchiveAsync("RefCreated");
        var activated = await CreateRawArchiveAsync("RefActivated", activate: true);
        var rollup = await CreateRollupAsync("RefRollup");
        await (await TenantAsync()).GetRollupArchiveLifecycleService()!.FreezeAsync(rollup, Until);
        var calls = new (string Name, Func<StreamDataController, Task<IActionResult>> Call)[]
        {
            ("activate", c => c.ActivateArchive(Tenant, $"{created}")),
            ("disable", c => c.DisableArchive(Tenant, $"{activated}")),
            ("enable", c => c.EnableArchive(Tenant, $"{activated}")),
            ("retry", c => c.RetryArchiveActivation(Tenant, $"{created}")),
            ("delete", c => c.DeleteArchive(Tenant, $"{activated}")),
            ("freeze", c => c.FreezeRollupArchive(Tenant, $"{rollup}", Until.AddDays(1))),
            ("unfreeze", c => c.UnfreezeRollupArchive(Tenant, $"{rollup}", acceptGaps: true)),
            ("rewind", c => c.RewindRollupWatermark(Tenant, $"{rollup}", Until.AddDays(-1)))
        };

        foreach (var (name, call) in calls)
        {
            _fixture.AuditEvents.Clear();
            var answer = await call(Controller(caller));

            var refused = answer.Should().BeOfType<ObjectResult>(name).Subject;
            refused.StatusCode.Should().Be(StatusCodes.Status403Forbidden, name);
            var problem = refused.Value.Should().BeOfType<ProblemDetails>(name).Subject;
            problem.Extensions["code"].Should().Be(CkMethodErrorCodes.MethodForbidden, name);
            if (expectedReason != null)
            {
                ((IReadOnlyDictionary<string, string>)problem.Extensions["parameters"]!)["reason"].Should()
                    .Be(expectedReason, name);
            }

            _fixture.AuditEvents.Should().ContainSingle(name).Which.Outcome.Should().Be(CkMethodErrorCodes.MethodForbidden);
        }

        (await SnapshotAsync(created))!.Status.Should().Be(CkArchiveStatus.Created);
        (await SnapshotAsync(activated))!.Status.Should().Be(CkArchiveStatus.Activated);
        var unchanged = await RollupAsync(rollup);
        unchanged.FrozenUntil.Should().Be(Until);
        unchanged.LastAggregatedBucketEnd.Should().NotBe(Until.AddDays(-1));
    }

    private async Task<ITenantContext> TenantAsync()
    {
        var systemContext = _fixture.GetSystemContext();
        return await systemContext.FindTenantContextAsync(systemContext.TenantId);
    }

    private async Task<ArchiveSnapshot?> SnapshotAsync(OctoObjectId rtId) =>
        await (await TenantAsync()).GetArchiveRuntimeStore().GetAsync(rtId);

    private async Task<RollupArchiveSnapshot> RollupAsync(OctoObjectId rtId) =>
        (await (await TenantAsync()).GetRollupArchiveRuntimeStore()!.GetAsync(rtId))!;

    private async Task<OctoObjectId> CreateRawArchiveAsync(string namePrefix, bool activate = false)
    {
        var archive = new RtRawArchive
        {
            RtWellKnownName = $"{namePrefix}{Guid.NewGuid():N}",
            TargetCkTypeId = _fixture.TestCkTypeId,
            Status = RtCkArchiveStatusEnum.Created,
            Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
            {
                new() { Path = "Voltage", Indexed = true, Required = false }
            }
        };
        var repository = _fixture.GetSystemContext().GetSystemTenantRepository();
        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            await repository.InsertOneRtEntityAsync(session, archive);
            await session.CommitTransactionAsync();
        }

        if (activate)
        {
            await (await TenantAsync()).GetArchiveLifecycleService()!.ActivateAsync(archive.RtId);
        }

        return archive.RtId;
    }

    /// <summary>An hourly rollup of a fresh raw source, in <c>Created</c>.</summary>
    private async Task<OctoObjectId> CreateRollupAsync(string prefix)
    {
        var source = await CreateRawArchiveAsync($"{prefix}Src", activate: true);
        return await (await TenantAsync()).GetRollupArchiveLifecycleService()!.CreateAsync($"{prefix}{Guid.NewGuid():N}",
            [new RollupSourceReference(source)], TimeSpan.FromHours(1), TimeSpan.Zero,
            [new CkRollupAggregationSpec("Voltage", CkRollupFunction.Sum, "voltageSum", null)],
            BucketAlignment.FixedSize, null, null, null);
    }

    /// <summary>The request services with another <see cref="IArchiveMethodFacade" /> (the switch-off case).</summary>
    private sealed class OverridingServiceProvider(IServiceProvider inner, IArchiveMethodFacade facade) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IArchiveMethodFacade) ? facade : inner.GetService(serviceType);
    }
}
