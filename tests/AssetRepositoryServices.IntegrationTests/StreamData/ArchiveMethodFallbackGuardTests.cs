using System.Security.Claims;
using FakeItEasy;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Methods.Audit;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
///     Every archive call that does not run through its CK method - the REST lifecycle, rollup, recompute and backfill
///     endpoints and the deprecated mutations while <see cref="IArchiveMethodFacade.CanServe" /> is false, and the
///     <c>recomputeArchive</c> / <c>backfillRollupFromSource</c> mutations always - runs behind
///     <see cref="IArchiveMethodFallbackGuard" />: a token of another tenant and a caller without data permission on the archive
///     are refused with the answer of the method path and change nothing, every call writes exactly one Method audit event
///     (transport <c>fallback</c>, also for a refusal) and one WARN line without the target id; an authorised caller keeps the
///     old answer. The controller is called without the HTTP tenant gate, i.e. as if the gate ran in LogOnly mode.
/// </summary>
[Collection(ArchiveMethodFacadeCollection.Name)]
public class ArchiveMethodFallbackGuardTests
{
    private static readonly DateTime Until = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime From = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly ArchiveMethodFacadeFixture _fixture;

    public ArchiveMethodFallbackGuardTests(ArchiveMethodFacadeFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    // ── REST on the fallback ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnTheFallback_ATokenOfAnotherTenant_IsRefusedOnEveryRestEndpoint_403TenantNotAllowed_AndNothingChanges()
    {
        var foreignAdmin = UserOf("some-other-tenant", CommonConstants.StreamDataAdminRole);
        await AssertEveryRestEndpointRefusedAsync(foreignAdmin, Fallback(), CkMethodTokenReasons.TenantNotAllowed);
    }

    [Fact]
    public async Task OnTheFallback_ACallerWithoutDataPermissionOnTheArchive_IsRefusedOnEveryRestEndpoint_AndNothingChanges()
    {
        await AssertEveryRestEndpointRefusedAsync(Admin(), Fallback(DenyingEvaluator()), "data-permission");
    }

    [Fact]
    public async Task OnTheFallback_AnAuthorisedCaller_Gets204_WithExactlyOneFallbackAuditEvent_AndOneWarnLineWithoutTheTarget()
    {
        var archive = await CreateRawArchiveAsync("FbOk");
        var rollup = await CreateRollupAsync("FbOkRollup");
        var log = new CapturingLogger();
        var services = Fallback(logger: log);

        await ExpectOneFallbackEventAsync(SystemStreamDataCkIds.ArchiveActivateMethodId, CkMethodAuditOutcomes.Success,
            async () => (await Controller(Admin(), services).ActivateArchive(Tenant, $"{archive}"))
                .Should().BeOfType<NoContentResult>());
        await ExpectOneFallbackEventAsync(SystemStreamDataCkIds.RollupArchiveFreezeMethodId, CkMethodAuditOutcomes.Success,
            async () => (await Controller(Admin(), services).FreezeRollupArchive(Tenant, $"{rollup}", Until))
                .Should().BeOfType<NoContentResult>());

        (await SnapshotAsync(archive))!.Status.Should().Be(CkArchiveStatus.Activated);
        (await RollupAsync(rollup)).FrozenUntil.Should().Be(Until);

        var warnings = log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
        warnings.Should().HaveCount(2, "one WARN line per call");
        warnings[0].Should().Contain("ActivateArchive").And.Contain(SystemStreamDataCkIds.ArchiveActivateMethodId)
            .And.Contain("method-not-declared").And.NotContain($"{archive}");
        warnings[1].Should().Contain("FreezeRollup").And.NotContain($"{rollup}");
    }

    [Fact]
    public async Task OnTheFallback_ATransitionTheStatusDoesNotAllow_StaysA400_AndIsAuditedWithItsCode()
    {
        var archive = await CreateRawArchiveAsync("FbBad");
        _fixture.AuditEvents.Clear();

        // Created -> Disable is not allowed: the direct call answers 400 as before, the audit names the refusal of the engine
        var answer = await Controller(Admin(), Fallback()).DisableArchive(Tenant, $"{archive}");

        answer.Should().BeOfType<BadRequestObjectResult>();
        _fixture.AuditEvents.Should().ContainSingle().Which.Should().Match<CkMethodAuditRecord>(e =>
            e.Transport == ArchiveMethodFallbackGuard.Transport &&
            e.Outcome == Statics.GraphQlErrorStreamDataInvalidTransition);
    }

    [Fact]
    public async Task WithTheSwitchOff_TheWarnLineNamesTheSwitch()
    {
        var archive = await CreateRawArchiveAsync("FbOff");
        var log = new CapturingLogger();
        var services = Fallback(logger: log, options: new StreamDataMethodsOptions { Enabled = false });

        (await Controller(Admin(), services).ActivateArchive(Tenant, $"{archive}")).Should().BeOfType<NoContentResult>();

        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Which.Message.Should().Contain("switch-off");
    }

    [Fact]
    public async Task OnTheFallback_AnArchiveTheCallerCannotSee_AnswersLikeAMissingOne_400OnRest_NotFoundOnTheMutation()
    {
        var viaRest = await CreateRawArchiveAsync("FbHidden");
        var viaMutation = await CreateRawArchiveAsync("FbHiddenMut");
        var services = Fallback(Evaluator(RtDataPermissionDecision.NotVisible));
        _fixture.AuditEvents.Clear();

        var rest = await Controller(Admin(), services).ActivateArchive(Tenant, $"{viaRest}");
        var mutation = await ExecuteAsync(
            $$"""mutation { streamData { activateArchive(rtId: "{{viaMutation}}") { archiveRtId } } }""", Admin(), services);

        rest.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().Be(new ArchiveNotFoundException(viaRest).Message);
        mutation.Errors!.First().Code.Should().Be(Statics.GraphQlErrorStreamDataArchiveNotFound);
        _fixture.AuditEvents.Should().HaveCount(2).And.OnlyContain(e =>
            e.Transport == ArchiveMethodFallbackGuard.Transport && e.Outcome == CkMethodErrorCodes.MethodTargetNotFound);
        (await SnapshotAsync(viaRest))!.Status.Should().Be(CkArchiveStatus.Created);
        (await SnapshotAsync(viaMutation))!.Status.Should().Be(CkArchiveStatus.Created);
    }

    [Fact]
    public async Task OnTheFallback_AServiceClientOnTheCrossTenantAllowList_Passes_AndAnotherServiceClientIsRefused()
    {
        var options = new TenantAuthorizationOptions();
        options.CrossTenantServiceClientIds.Add("platform-service");
        var services = Fallback(tenantOptions: options);
        var allowListed = await CreateRawArchiveAsync("FbAllowed");
        var foreign = await CreateRawArchiveAsync("FbForeignService");

        (await Controller(ServiceOf("platform-service"), services).ActivateArchive(Tenant, $"{allowListed}"))
            .Should().BeOfType<NoContentResult>();
        var refused = await Controller(ServiceOf("other-service"), services).ActivateArchive(Tenant, $"{foreign}");

        refused.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        (await SnapshotAsync(allowListed))!.Status.Should().Be(CkArchiveStatus.Activated);
        (await SnapshotAsync(foreign))!.Status.Should().Be(CkArchiveStatus.Created);
    }

    [Fact]
    public async Task OnTheFallback_WithAStrictAuditThatFails_AnExecutedCallIsOutcomeUnknown_AndARefusalStaysTheRefusal()
    {
        var archive = await CreateRawArchiveAsync("FbStrict");
        var foreign = await CreateRawArchiveAsync("FbStrictForeign");
        var failingRecorder = A.Fake<ICkMethodAuditRecorder>();
        A.CallTo(() => failingRecorder.RecordAsync(A<CkMethodAuditRecord>._, A<CancellationToken>._))
            .ThrowsAsync(new CkMethodAuditFailedException(Guid.NewGuid(), new TimeoutException()));
        var services = Fallback(auditRecorder: failingRecorder);

        // like the audit filter of the method pipeline: the change ran, its event is missing, so the outcome is unknown
        var executed = await Controller(Admin(), services).ActivateArchive(Tenant, $"{archive}");
        var refused = await Controller(UserOf("some-other-tenant", CommonConstants.StreamDataAdminRole), services)
            .ActivateArchive(Tenant, $"{foreign}");

        var unknown = executed.Should().BeOfType<ObjectResult>().Subject;
        unknown.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        unknown.Value.Should().BeOfType<ProblemDetails>().Which.Extensions["code"].Should()
            .Be(CkMethodErrorCodes.MethodOutcomeUnknown);
        var stillRefused = refused.Should().BeOfType<ObjectResult>().Subject;
        stillRefused.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        stillRefused.Value.Should().BeOfType<ProblemDetails>().Which.Extensions["code"].Should()
            .Be(CkMethodErrorCodes.MethodForbidden);
        (await SnapshotAsync(foreign))!.Status.Should().Be(CkArchiveStatus.Created);
    }

    [Fact]
    public async Task OnTheFallback_AnEngineRefusal_Stays400WithTheEngineMessage_WithOnlyTheGuardsWarnLine_WithoutTheTarget()
    {
        var archive = await CreateRawArchiveAsync("FbEngRef");
        var rollup = await CreateRollupAsync("FbEngRefRollup", activate: true);
        // the lifecycle call is refused by the real engine (Created -> Disable is not allowed); the recompute and the backfill
        // run against a real rollup whose direct call the engine refuses (the orchestrator itself answers most errors with
        // a Failed job, so the refusal is injected at the direct call the guard runs)
        var refusal = new ArchiveNotActivatedException(rollup, CkArchiveStatus.Created);
        var calls = new (string Name, string TargetId, string? Message, Func<IServiceProvider, Task<IActionResult>> Call)[]
        {
            ("lifecycle (disable)", $"{archive}", null,
                services => Controller(Admin(), services, Log).DisableArchive(Tenant, $"{archive}")),
            ("recompute", $"{rollup}", refusal.Message, async services =>
                (await Controller(Admin(), services, Log).RecomputeArchive(Tenant, $"{rollup}", From, From.AddDays(1), null))
                .Result!),
            ("backfill-from-source", $"{rollup}", refusal.Message, async services =>
                (await Controller(Admin(), services, Log).BackfillRollupFromSource(Tenant, $"{rollup}")).Result!)
        };

        foreach (var (name, targetId, message, call) in calls)
        {
            Log.Entries.Clear();
            var services = message == null ? Fallback(logger: Log) : Fallback(logger: Log, engineRefusal: refusal);

            var answer = await call(services);

            var badRequest = answer.Should().BeOfType<BadRequestObjectResult>(name).Subject;
            var body = badRequest.Value.Should().BeOfType<string>(name).Subject;
            body.Should().NotBeNullOrEmpty(name);
            if (message != null)
            {
                body.Should().Be(message, $"{name}: the caller still gets the engine message");
            }

            var warnings = Log.Entries.Where(e => e.Level >= LogLevel.Warning).ToList();
            warnings.Should().ContainSingle($"{name}: only the guard's WARN line").Which.Message.Should()
                .Contain("ran on the fallback of", name);
            warnings.Should().OnlyContain(e => !e.Message.Contains(targetId) && !e.Message.Contains(body),
                $"{name}: no line at Warning or above names the target id or the engine message");
        }

        (await SnapshotAsync(archive))!.Status.Should().Be(CkArchiveStatus.Created);
        (await (await TenantAsync()).GetRecomputeJobStore()!.GetForArchiveAsync(rollup, 10))
            .Should().BeEmpty("the refused calls start no recompute and queue no backfill");
    }

    [Fact]
    public async Task OnTheFallback_WithAStrictAuditThatFails_AnEngineRefusalIsOutcomeUnknown_OnRestAndOnTheDeprecatedMutation()
    {
        var archive = await CreateRawArchiveAsync("FbStrictEng");
        var rollup = await CreateRollupAsync("FbStrictEngRollup", activate: true);
        var failingRecorder = A.Fake<ICkMethodAuditRecorder>();
        A.CallTo(() => failingRecorder.RecordAsync(A<CkMethodAuditRecord>._, A<CancellationToken>._))
            .ThrowsAsync(new CkMethodAuditFailedException(Guid.NewGuid(), new TimeoutException()));
        var services = Fallback(auditRecorder: failingRecorder);

        // like the audit filter of the method pipeline: the handler answers an engine refusal as a STREAMDATA_* failure,
        // which is no "not executed" code, so a failed strict audit write turns it into METHOD_OUTCOME_UNKNOWN
        var rest = await Controller(Admin(), services).DisableArchive(Tenant, $"{archive}");
        var recompute = (await Controller(Admin(), Fallback(auditRecorder: failingRecorder,
                engineRefusal: new ArchiveNotActivatedException(rollup, CkArchiveStatus.Created)))
            .RecomputeArchive(Tenant, $"{rollup}", From, From.AddDays(1), null)).Result;
        var mutation = await ExecuteAsync($$"""mutation { streamData { disableArchive(rtId: "{{archive}}") { archiveRtId } } }""",
            Admin(), services);

        foreach (var (name, answer) in new[] { ("rest disable", rest), ("rest recompute", recompute!) })
        {
            var unknown = answer.Should().BeOfType<ObjectResult>(name).Subject;
            unknown.StatusCode.Should().Be(StatusCodes.Status500InternalServerError, name);
            unknown.Value.Should().BeOfType<ProblemDetails>(name).Which.Extensions["code"].Should()
                .Be(CkMethodErrorCodes.MethodOutcomeUnknown, name);
        }

        mutation.Errors.Should().NotBeNullOrEmpty();
        mutation.Errors!.First().Code.Should().Be(CkMethodErrorCodes.MethodOutcomeUnknown);
        (await SnapshotAsync(archive))!.Status.Should().Be(CkArchiveStatus.Created, "the engine refused the transition");
    }

    // ── the deprecated lifecycle and rollup mutations on the fallback ─────────────────────────────

    [Fact]
    public async Task OnTheFallback_TheDeprecatedMutations_RefuseATokenOfAnotherTenant_AndACallerWithoutDataPermission()
    {
        var callers = new (string Name, ClaimsPrincipal User, IServiceProvider Services, string Reason)[]
        {
            ("foreign tenant", UserOf("some-other-tenant", CommonConstants.StreamDataAdminRole), Fallback(),
                CkMethodTokenReasons.TenantNotAllowed),
            ("no data permission", Admin(), Fallback(DenyingEvaluator()), "data-permission")
        };

        foreach (var (name, user, services, reason) in callers)
        {
            var archive = await CreateRawArchiveAsync("FbMut");
            var rollup = await CreateRollupAsync("FbMutRollup");
            var mutations = new[]
            {
                $$"""mutation { streamData { activateArchive(rtId: "{{archive}}") { archiveRtId } } }""",
                $$"""mutation { streamData { freezeRollupArchive(rtId: "{{rollup}}", until: "{{Until:O}}") { archiveRtId } } }""",
                $$"""mutation { streamData { deleteArchive(rtId: "{{archive}}") } }"""
            };

            foreach (var mutation in mutations)
            {
                _fixture.AuditEvents.Clear();
                var result = await ExecuteAsync(mutation, user, services);

                AssertRefused(result, reason, $"{name}: {mutation}");
                _fixture.AuditEvents.Should().ContainSingle(name).Which.Should().Match<CkMethodAuditRecord>(e =>
                    e.Transport == ArchiveMethodFallbackGuard.Transport && e.Outcome == CkMethodErrorCodes.MethodForbidden);
            }

            (await SnapshotAsync(archive))!.Status.Should().Be(CkArchiveStatus.Created, name);
            (await RollupAsync(rollup)).FrozenUntil.Should().BeNull(name);
        }
    }

    // ── recomputeArchive and backfillRollupFromSource: always the direct call, always guarded ──────

    [Fact]
    public async Task TheRecomputeAndBackfillMutations_RefuseATokenOfAnotherTenant_AndNoJobStarts()
    {
        // the real host: the facade could serve Recompute-2, but these mutations are no facade of it
        await AssertRecomputeMutationsRefusedAsync(UserOf("some-other-tenant", CommonConstants.StreamDataAdminRole), null,
            CkMethodTokenReasons.TenantNotAllowed);
    }

    [Fact]
    public async Task TheRecomputeAndBackfillMutations_RefuseACallerWithoutDataPermission_AndNoJobStarts()
    {
        await AssertRecomputeMutationsRefusedAsync(Admin(), Fallback(DenyingEvaluator()), "data-permission");
    }

    [Fact]
    public async Task TheRecomputeMutation_ForAnAuthorisedCaller_AnswersTheJobAsBefore_WithOneAuditEventMarkedNotRouted()
    {
        var rollup = await CreateRollupAsync("FbRecOk", activate: true);
        var log = new CapturingLogger();
        _fixture.AuditEvents.Clear();

        var result = await ExecuteAsync(
            $$"""mutation { streamData { recomputeArchive(rtId: "{{rollup}}", from: "{{From:O}}", to: "{{From.AddDays(1):O}}") { state } } }""",
            Admin(), Fallback(logger: log));

        result.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(result));
        JObject.Parse(_fixture.SerializeGraphQl(result)).SelectToken("data.streamData.recomputeArchive.state")!
            .Value<string>().Should().Be("Completed");
        _fixture.AuditEvents.Should().ContainSingle().Which.Should().Match<CkMethodAuditRecord>(e =>
            e.MethodId == SystemStreamDataCkIds.RollupArchiveRecompute2MethodId &&
            e.Transport == ArchiveMethodFallbackGuard.Transport && e.Outcome == CkMethodAuditOutcomes.Success);
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Which.Message.Should().Contain("not-routed")
            .And.Contain("recomputeArchive").And.NotContain($"{rollup}");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private string Tenant => _fixture.GetSystemContext().TenantId;

    /// <summary>One sink for the lines of the guard and of the controller.</summary>
    private CapturingLogger Log { get; } = new();

    private ClaimsPrincipal Admin() => UserOf(Tenant, CommonConstants.StreamDataAdminRole);

    private static ClaimsPrincipal UserOf(string tokenTenant, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("sub", "65f0c0ffee65f0c0ffee9337"), new("scope", "octo_api openid"), new(ClaimTypes.Name, "archive-admin"),
            new("tenant_id", tokenTenant)
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim("role", role));
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    /// <summary>A data-permission evaluator that refuses every action on a visible archive (a policy without a grant).</summary>
    private static IRtDataPermissionEvaluator DenyingEvaluator() => Evaluator(RtDataPermissionDecision.NotPermitted);

    private static IRtDataPermissionEvaluator Evaluator(RtDataPermissionDecision decision)
    {
        var evaluator = A.Fake<IRtDataPermissionEvaluator>();
        A.CallTo(() => evaluator.EvaluateAsync(A<IRuntimeRepository>._, A<RtSecurityContext>._, A<RtEntityId>._,
                A<RtDataAction>._, A<bool>._, A<CancellationToken>._))
            .Returns(new RtDataPermissionVerdict(decision, RtDataPermissionReason.NoGrant));
        return evaluator;
    }

    /// <summary>A client-credentials token (no <c>sub</c>) of a service client issued for another tenant.</summary>
    private static ClaimsPrincipal ServiceOf(string clientId) => new(new ClaimsIdentity(
    [
        new Claim("client_id", clientId), new Claim("scope", "octo_api"),
        new Claim("role", CommonConstants.StreamDataAdminRole), new Claim(ClaimTypes.Role, CommonConstants.StreamDataAdminRole),
        new Claim("tenant_id", "othertenant")
    ], "Bearer"));

    /// <summary>
    ///     The request services of a tenant on the fallback: a facade that cannot serve, and the real guard of the host or one
    ///     built from the host's services with another evaluator, logger or switch.
    /// </summary>
    private IServiceProvider Fallback(IRtDataPermissionEvaluator? evaluator = null, ILogger<ArchiveMethodFallbackGuard>? logger = null,
        StreamDataMethodsOptions? options = null, TenantAuthorizationOptions? tenantOptions = null,
        ICkMethodAuditRecorder? auditRecorder = null, StreamDataException? engineRefusal = null)
    {
        var inner = _fixture.GetService<IServiceScopeFactory>().CreateScope().ServiceProvider;
        var facade = A.Fake<IArchiveMethodFacade>();
        A.CallTo(() => facade.IsHosted).Returns(options?.Enabled ?? true);
        A.CallTo(() => facade.CanServe(A<ITenantContext>._, A<string>._, A<string>._)).Returns(false);
        var guard = evaluator == null && logger == null && options == null && tenantOptions == null && auditRecorder == null
            ? inner.GetRequiredService<IArchiveMethodFallbackGuard>()
            : new ArchiveMethodFallbackGuard(evaluator ?? inner.GetRequiredService<IRtDataPermissionEvaluator>(),
                inner.GetRequiredService<ICkMethodAuthorizationDefinitionSource>(),
                auditRecorder ?? inner.GetRequiredService<ICkMethodAuditRecorder>(),
                options != null ? Options.Create(options) : inner.GetRequiredService<IOptions<StreamDataMethodsOptions>>(),
                logger ?? NullLogger<ArchiveMethodFallbackGuard>.Instance,
                tenantOptions != null ? Options.Create(tenantOptions) : inner.GetService<IOptions<TenantAuthorizationOptions>>(),
                inner.GetService<ITenantHierarchyReader>());
        return new OverridingServiceProvider(inner, facade,
            engineRefusal == null ? guard : new EngineRefusingGuard(guard, engineRefusal));
    }

    private StreamDataController Controller(ClaimsPrincipal user, IServiceProvider services,
        ILogger<StreamDataController>? logger = null)
    {
        var controller = new StreamDataController(logger ?? NullLogger<StreamDataController>.Instance,
            _fixture.GetSystemContext(),
            A.Fake<IHostApplicationLifetime>());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user, RequestServices = services }
        };
        return controller;
    }

    private async Task<ExecutionResult> ExecuteAsync(string mutation, ClaimsPrincipal user, IServiceProvider? services)
    {
        if (services == null)
        {
            return await _fixture.ExecuteGraphQlAsync(mutation, user: user);
        }

        var tenant = await TenantAsync();
        return await _fixture.GetService<IDocumentExecuter<OctoSchema>>().ExecuteAsync(options =>
        {
            options.Schema = null;
            options.Query = mutation;
            options.RequestServices = services;
            options.UserContext = new GraphQlUserContext(user, tenant);
        });
    }

    private void AssertRefused(ExecutionResult result, string reason, string because)
    {
        result.Errors.Should().NotBeNullOrEmpty(because);
        result.Errors!.First().Code.Should().Be(CkMethodErrorCodes.MethodForbidden, because);
        JObject.Parse(_fixture.SerializeGraphQl(result)).SelectToken("errors[0].extensions.parameters.reason")!
            .Value<string>().Should().Be(reason, because);
    }

    private async Task ExpectOneFallbackEventAsync(string methodId, string outcome, Func<Task> call)
    {
        _fixture.AuditEvents.Clear();
        await call();
        _fixture.AuditEvents.Should().ContainSingle(methodId).Which.Should().Match<CkMethodAuditRecord>(e =>
            e.MethodId == methodId && e.Outcome == outcome && e.Transport == ArchiveMethodFallbackGuard.Transport &&
            e.TenantId == Tenant && e.HandlerInstanceId == null);
    }

    /// <summary>Every REST endpoint on the fallback refused with 403 METHOD_FORBIDDEN and the reason, one fallback audit event each, nothing changed.</summary>
    private async Task AssertEveryRestEndpointRefusedAsync(ClaimsPrincipal caller, IServiceProvider services, string reason)
    {
        var created = await CreateRawArchiveAsync("FbRefCreated");
        var activated = await CreateRawArchiveAsync("FbRefActivated", activate: true);
        var rollup = await CreateRollupAsync("FbRefRollup", activate: true);
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
            ("rewind", c => c.RewindRollupWatermark(Tenant, $"{rollup}", Until.AddDays(-1))),
            ("recompute", async c => (await c.RecomputeArchive(Tenant, $"{rollup}", From, From.AddDays(1), null)).Result!),
            ("backfill", async c => (await c.BackfillRollupFromSource(Tenant, $"{rollup}")).Result!)
        };

        foreach (var (name, call) in calls)
        {
            _fixture.AuditEvents.Clear();
            var answer = await call(Controller(caller, services));

            var refused = answer.Should().BeOfType<ObjectResult>(name).Subject;
            refused.StatusCode.Should().Be(StatusCodes.Status403Forbidden, name);
            var problem = refused.Value.Should().BeOfType<ProblemDetails>(name).Subject;
            problem.Extensions["code"].Should().Be(CkMethodErrorCodes.MethodForbidden, name);
            ((IReadOnlyDictionary<string, string>)problem.Extensions["parameters"]!)["reason"].Should().Be(reason, name);
            _fixture.AuditEvents.Should().ContainSingle(name).Which.Should().Match<CkMethodAuditRecord>(e =>
                e.Transport == ArchiveMethodFallbackGuard.Transport && e.Outcome == CkMethodErrorCodes.MethodForbidden);
        }

        (await SnapshotAsync(created))!.Status.Should().Be(CkArchiveStatus.Created);
        (await SnapshotAsync(activated))!.Status.Should().Be(CkArchiveStatus.Activated);
        var unchanged = await RollupAsync(rollup);
        unchanged.FrozenUntil.Should().Be(Until);
        unchanged.LastAggregatedBucketEnd.Should().NotBe(Until.AddDays(-1));
        (await (await TenantAsync()).GetRecomputeJobStore()!.GetForArchiveAsync(rollup, 10))
            .Should().BeEmpty("a refused call starts no recompute and queues no backfill");
    }

    private async Task AssertRecomputeMutationsRefusedAsync(ClaimsPrincipal caller, IServiceProvider? services, string reason)
    {
        var rollup = await CreateRollupAsync("FbRecRef", activate: true);
        var mutations = new[]
        {
            $$"""mutation { streamData { recomputeArchive(rtId: "{{rollup}}", from: "{{From:O}}", to: "{{From.AddDays(1):O}}") { state } } }""",
            $$"""mutation { streamData { backfillRollupFromSource(rtId: "{{rollup}}") { state } } }"""
        };

        foreach (var mutation in mutations)
        {
            _fixture.AuditEvents.Clear();
            var result = await ExecuteAsync(mutation, caller, services);

            AssertRefused(result, reason, mutation);
            _fixture.AuditEvents.Should().ContainSingle(mutation).Which.Should().Match<CkMethodAuditRecord>(e =>
                e.Transport == ArchiveMethodFallbackGuard.Transport && e.Outcome == CkMethodErrorCodes.MethodForbidden);
        }

        (await (await TenantAsync()).GetRecomputeJobStore()!.GetForArchiveAsync(rollup, 10))
            .Should().BeEmpty("a refused call starts no recompute and queues no backfill");
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

    /// <summary>An hourly rollup of a fresh, activated raw source (empty).</summary>
    private async Task<OctoObjectId> CreateRollupAsync(string prefix, bool activate = false)
    {
        var source = await CreateRawArchiveAsync($"{prefix}Src", activate: true);
        var tenant = await TenantAsync();
        var rollup = await tenant.GetRollupArchiveLifecycleService()!.CreateAsync($"{prefix}{Guid.NewGuid():N}",
            [new RollupSourceReference(source)], TimeSpan.FromHours(1), TimeSpan.Zero,
            [new CkRollupAggregationSpec("Voltage", CkRollupFunction.Sum, "voltageSum", null)],
            BucketAlignment.FixedSize, null, null, null);
        if (activate)
        {
            await tenant.GetArchiveLifecycleService()!.ActivateAsync(rollup);
        }

        return rollup;
    }

    /// <summary>The request services with another facade and another fallback guard.</summary>
    private sealed class OverridingServiceProvider(IServiceProvider inner, IArchiveMethodFacade facade,
        IArchiveMethodFallbackGuard guard) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IArchiveMethodFacade) ? facade
            : serviceType == typeof(IArchiveMethodFallbackGuard) ? guard
            : inner.GetService(serviceType);
    }

    /// <summary>
    ///     The real guard, with the direct call replaced by an engine refusal: the guard's checks, audit and WARN line run
    ///     unchanged, then the direct call throws <paramref name="refusal" /> as the engine would.
    /// </summary>
    private sealed class EngineRefusingGuard(IArchiveMethodFallbackGuard inner, StreamDataException refusal)
        : IArchiveMethodFallbackGuard
    {
        public Task<ArchiveMethodFallbackResult<T>> InvokeAsync<T>(ITenantContext tenant, string ckTypeId, string methodId,
            string operation, OctoObjectId targetRtId, ClaimsPrincipal user, Func<Task<T>> direct,
            ArchiveMethodFallbackReason? reason = null, CancellationToken cancellationToken = default) =>
            inner.InvokeAsync(tenant, ckTypeId, methodId, operation, targetRtId, user, () => Task.FromException<T>(refusal),
                reason, cancellationToken);
    }

    /// <summary>Collects the formatted log lines of the guard and of the controller.</summary>
    private sealed class CapturingLogger : ILogger<ArchiveMethodFallbackGuard>, ILogger<StreamDataController>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
