using System.Security.Claims;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
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
using Meshmakers.Octo.Services.Methods.Audit;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
///     the six REST column and computed-column endpoints of <see cref="StreamDataController" /> are facades of the
///     Archive column methods, against the real host (MongoDB, CrateDB, gateway, in-process handlers). Per
///     endpoint: the method's change with the REST answer of before (<c>204</c>, or the queued job for retry and repair) and
///     exactly one Method audit event; the handler starts without the request session; a Hidden attribute is refused as
///     before; a token of another tenant and a caller without <c>StreamDataAdmin</c> are refused with <c>403</c>
///     and change nothing, on the method path and on the fallback; with the switch off every endpoint keeps its direct call
///     behind the fallback guard.
/// </summary>
[Collection(ArchiveMethodFacadeCollection.Name)]
public class ColumnMethodFacadeTests
{
    private const string AccessTestAccount = "AssetRepositoryIntegrationTest/AccessTestAccount";

    private readonly ArchiveMethodFacadeFixture _fixture;

    public ColumnMethodFacadeTests(ArchiveMethodFacadeFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    // ── the six endpoints: method parity, the answer of before, one audit event each ───────────

    [Fact]
    public async Task EveryColumnEndpoint_RunsItsMethod_AnswersAsBefore_WithExactlyOneAuditEvent()
    {
        var archive = await CreateRawArchiveAsync("RestCols", activate: true);
        var failed = await CreateArchiveWithFailedComputedColumnAsync("RestRetry", "broken");
        var active = await CreateArchiveWithActiveComputedColumnAsync("RestRepair", "fixed");

        await ExpectNoContentAsync(SystemStreamDataCkIds.ArchiveAddComputedColumnMethodId,
            c => c.AddComputedColumn(Tenant, $"{archive}", "doubled", "Voltage * 2", FormulaResultType.Double, indexed: false));
        var added = await ColumnAsync(archive, "doubled");
        (added.ResultType, added.Indexed).Should().Be((FormulaResultType.Double, false));
        added.ComputedState.Should().BeOneOf(ComputedColumnState.Backfilling, ComputedColumnState.Active);

        await ExpectNoContentAsync(SystemStreamDataCkIds.ArchiveAddColumnMethodId,
            c => c.AddArchiveColumn(Tenant, $"{archive}", "Current", indexed: true));
        (await SnapshotAsync(archive))!.Columns.Should().Contain(c => c.Path == "Current" && c.Indexed);

        await DrainUntilAsync(archive, "doubled", ComputedColumnState.Active);
        await ExpectNoContentAsync(SystemStreamDataCkIds.ArchiveUpdateComputedColumnFormulaMethodId,
            c => c.UpdateComputedColumnFormula(Tenant, $"{archive}", "doubled", "Voltage * 3"));
        var updated = await ColumnAsync(archive, "doubled");
        (updated.PendingFormula ?? updated.Formula).Should().BeEquivalentTo("Voltage * 3");

        await DrainUntilAsync(archive, "doubled", ComputedColumnState.Active);
        await ExpectNoContentAsync(SystemStreamDataCkIds.ArchiveRemoveComputedColumnMethodId,
            c => c.RemoveComputedColumn(Tenant, $"{archive}", "doubled"));
        (await SnapshotAsync(archive))!.Columns.Should().NotContain(c => c.Name == "doubled");

        var retried = await ExpectJobAsync(SystemStreamDataCkIds.ArchiveRetryComputedColumnBackfillMethodId,
            c => c.RetryComputedColumnBackfill(Tenant, $"{failed}", "broken"));
        retried.ComputedColumnName.Should().Be("broken");
        (await ColumnAsync(failed, "broken")).ComputedState.Should()
            .BeOneOf(ComputedColumnState.Backfilling, ComputedColumnState.Active);

        var repaired = await ExpectJobAsync(SystemStreamDataCkIds.ArchiveRepairComputedColumnMethodId,
            c => c.RepairComputedColumn(Tenant, $"{active}", "fixed", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
                null));
        repaired.ComputedColumnName.Should().Be("fixed");
        var job = await (await TenantAsync()).GetRecomputeJobStore()!.GetAsync(new OctoObjectId(repaired.RtId));
        job.Should().NotBeNull("the answered job is the queued repair job");
        job!.IsComputedColumnRepair.Should().BeTrue();
        repaired.Should().BeEquivalentTo(RecomputeJobInfoRestDto.From(job), o => o.Excluding(j => j.State)
            .Excluding(j => j.StartedAt).Excluding(j => j.LastProgressAt).Excluding(j => j.RowsProcessed)
            .Excluding(j => j.WindowsProcessed).Excluding(j => j.FinishedAt).Excluding(j => j.DurationMs),
            "the answer is the job the method queued; only its progress may have moved on");
    }

    [Fact]
    public async Task TheHandler_RunsOutsideTheRequestSession_EvenWhenTheCallerHoldsOne()
    {
        var archive = await CreateRawArchiveAsync("ColSession", activate: true);
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

            var answer = await Controller(Admin(), scope.ServiceProvider)
                .AddArchiveColumn(Tenant, $"{archive}", "Current", indexed: false);

            answer.Should().BeOfType<NoContentResult>();
            _fixture.SessionVisibleAtHandlerEntry.Should().ContainSingle().Which.Should()
                .BeFalse("the in-process handler starts without the request session");
            accessor.HasSession.Should().BeTrue("the request session is untouched");
            (await SnapshotAsync(archive))!.Columns.Select(c => c.Path).Should().Equal("Voltage", "Current");
        }
        finally
        {
            await accessor.AbortAsync();
        }
    }

    // ── Hidden attribute, refusals with the message of before ──────────────────────────────────

    [Fact]
    public async Task AHiddenAttribute_IsRefusedAsBefore_400WithTheEngineMessage_OnTheMethodAndTheFallback()
    {
        var viaMethod = await CreateArchiveOfAsync(AccessTestAccount, "Name", "HiddenMeth");
        var viaFallback = await CreateArchiveOfAsync(AccessTestAccount, "Name", "HiddenFallback");

        _fixture.AuditEvents.Clear();
        var method = await Controller(Admin()).AddArchiveColumn(Tenant, $"{viaMethod}", "PasswordHash");
        _fixture.AuditEvents.Should().ContainSingle().Which.Should().Match<CkMethodAuditRecord>(e =>
            e.MethodId == SystemStreamDataCkIds.ArchiveAddColumnMethodId && e.Outcome == Statics.GraphQlErrorStreamData &&
            e.Transport != ArchiveMethodFallbackGuard.Transport);

        _fixture.AuditEvents.Clear();
        var fallback = await Controller(Admin(), SwitchedOff()).AddArchiveColumn(Tenant, $"{viaFallback}", "PasswordHash");
        _fixture.AuditEvents.Should().ContainSingle().Which.Should().Match<CkMethodAuditRecord>(e =>
            e.MethodId == SystemStreamDataCkIds.ArchiveAddColumnMethodId && e.Outcome == Statics.GraphQlErrorStreamData &&
            e.Transport == ArchiveMethodFallbackGuard.Transport);

        foreach (var answer in new[] { method, fallback })
        {
            answer.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().BeOfType<string>().Which.Should()
                .Contain("Hidden attribute 'PasswordHash'");
        }

        (await SnapshotAsync(viaMethod))!.Columns.Select(c => c.Path).Should().Equal("Name");
        (await SnapshotAsync(viaFallback))!.Columns.Select(c => c.Path).Should().Equal("Name");
    }

    [Fact]
    public async Task ARefusedColumnChange_Answers400WithTheEngineMessage_AsTheDirectCall()
    {
        // retry of an Active column: the engine refuses it; the method answers STREAMDATA_ERROR with the engine text, the
        // facade the 400 with that text as before
        var viaMethod = await CreateArchiveWithActiveComputedColumnAsync("RetryActiveMeth", "settled");
        var viaFallback = await CreateArchiveWithActiveComputedColumnAsync("RetryActiveFallback", "settled");

        var method = await Controller(Admin()).RetryComputedColumnBackfill(Tenant, $"{viaMethod}", "settled");
        var fallback = await Controller(Admin(), SwitchedOff()).RetryComputedColumnBackfill(Tenant, $"{viaFallback}", "settled");

        var methodMessage = method.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value.Should().BeOfType<string>()
            .Subject;
        var fallbackMessage = fallback.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value.Should()
            .BeOfType<string>().Subject;
        methodMessage.Should().Be(fallbackMessage.Replace(viaFallback.ToString(), viaMethod.ToString(), StringComparison.Ordinal),
            "the method answers the engine message the direct call answered");
        (await ColumnAsync(viaMethod, "settled")).ComputedState.Should().Be(ComputedColumnState.Active);
    }

    [Fact]
    public async Task AnUnknownComputedColumn_Is400WithTheEngineMessage_OnEveryChangingEndpoint_OnBothPaths()
    {
        // behaviour change (CHANGELOG): update of an unknown column answered an unhandled 500 before;
        // the method refuses it as STREAMDATA_ERROR, so REST answers 400 with the engine message on both paths
        var archive = await CreateRawArchiveAsync("UnknownCol", activate: true);
        foreach (var services in new IServiceProvider?[] { null, SwitchedOff() })
        {
            var calls = new (string Name, Func<StreamDataController, Task<IActionResult>> Call)[]
            {
                ("update", c => c.UpdateComputedColumnFormula(Tenant, $"{archive}", "nope", "Voltage")),
                ("retry", async c => (await c.RetryComputedColumnBackfill(Tenant, $"{archive}", "nope")).Result!),
                ("repair", async c => (await c.RepairComputedColumn(Tenant, $"{archive}", "nope", null, null)).Result!)
            };
            foreach (var (name, call) in calls)
            {
                var answer = await call(Controller(Admin(), services));
                answer.Should().BeOfType<BadRequestObjectResult>(name).Which.Value.Should().BeOfType<string>(name).Which
                    .Should().Contain("nope", name);
            }
        }

        (await SnapshotAsync(archive))!.Columns.Select(c => c.Path).Should().Equal("Voltage");
    }

    [Fact]
    public async Task AResultTypeOutsideTheEnum_IsA400ValidationFailure_AndAddsNothing()
    {
        var archive = await CreateRawArchiveAsync("BadResultType", activate: true);

        var answer = await Controller(Admin())
            .AddComputedColumn(Tenant, $"{archive}", "odd", "Voltage", (FormulaResultType)999);

        var refused = answer.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        refused.Value.Should().BeOfType<ProblemDetails>().Which.Extensions["code"].Should()
            .Be(CkMethodErrorCodes.MethodValidationFailed);
        (await SnapshotAsync(archive))!.Columns.Select(c => c.Path).Should().Equal("Voltage");
    }

    [Fact]
    public async Task AMissingArchive_StaysA400WithTheNotFoundMessage()
    {
        var missing = OctoObjectId.GenerateNewId();

        var add = await Controller(Admin()).AddArchiveColumn(Tenant, $"{missing}", "Current");
        var repair = await Controller(Admin()).RepairComputedColumn(Tenant, $"{missing}", "x", null, null);

        add.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().Be(new ArchiveNotFoundException(missing).Message);
        repair.Result.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should()
            .Be(new ArchiveNotFoundException(missing).Message);
    }

    // ── abuse cases ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ATokenOfAnotherTenant_IsRefusedOnEveryColumnEndpoint_403TenantNotAllowed_AndNothingChanges()
    {
        var foreignAdmin = UserOf("some-other-tenant", CommonConstants.StreamDataAdminRole);
        await AssertEveryEndpointRefusedAsync(foreignAdmin, CkMethodTokenReasons.TenantNotAllowed, services: null);
    }

    [Fact]
    public async Task OnTheFallback_ATokenOfAnotherTenant_IsRefusedOnEveryColumnEndpoint_403TenantNotAllowed_AndNothingChanges()
    {
        var foreignAdmin = UserOf("some-other-tenant", CommonConstants.StreamDataAdminRole);
        await AssertEveryEndpointRefusedAsync(foreignAdmin, CkMethodTokenReasons.TenantNotAllowed, SwitchedOff(),
            ArchiveMethodFallbackGuard.Transport);
    }

    [Fact]
    public async Task ACallerWithoutStreamDataAdmin_IsRefusedOnEveryColumnEndpoint_403_AndNothingChanges()
    {
        // the REST policy refuses this caller before the action (ColumnMethodFacadeHttpTests); the method's declared role is
        // the second line
        await AssertEveryEndpointRefusedAsync(User("SomeOtherRole"), expectedReason: null, services: null);
    }

    // ── Methods:StreamData:Enabled=false ────────────────────────────────────────────────────────

    [Fact]
    public async Task WithTheSwitchOff_EveryColumnEndpoint_KeepsItsDirectCall_WithOneFallbackAuditEvent()
    {
        var archive = await CreateRawArchiveAsync("OffCols", activate: true);
        var failed = await CreateArchiveWithFailedComputedColumnAsync("OffRetry", "broken");
        var active = await CreateArchiveWithActiveComputedColumnAsync("OffRepair", "fixed");
        var services = SwitchedOff();
        var calls = new (string MethodId, Func<StreamDataController, Task<IActionResult>> Call, Type Answer)[]
        {
            (SystemStreamDataCkIds.ArchiveAddComputedColumnMethodId,
                c => c.AddComputedColumn(Tenant, $"{archive}", "doubled", "Voltage * 2", FormulaResultType.Double), typeof(NoContentResult)),
            (SystemStreamDataCkIds.ArchiveAddColumnMethodId, c => c.AddArchiveColumn(Tenant, $"{archive}", "Current"),
                typeof(NoContentResult)),
            (SystemStreamDataCkIds.ArchiveRemoveComputedColumnMethodId,
                c => c.RemoveComputedColumn(Tenant, $"{active}", "absent"), typeof(NoContentResult)),
            (SystemStreamDataCkIds.ArchiveUpdateComputedColumnFormulaMethodId,
                c => c.UpdateComputedColumnFormula(Tenant, $"{active}", "fixed", "Voltage * 4"), typeof(NoContentResult)),
            (SystemStreamDataCkIds.ArchiveRetryComputedColumnBackfillMethodId,
                async c => (await c.RetryComputedColumnBackfill(Tenant, $"{failed}", "broken")).Result!, typeof(OkObjectResult)),
            (SystemStreamDataCkIds.ArchiveRepairComputedColumnMethodId,
                async c => (await c.RepairComputedColumn(Tenant, $"{failed}", "broken", null, null)).Result!, typeof(BadRequestObjectResult))
        };

        foreach (var (methodId, call, answerType) in calls)
        {
            _fixture.AuditEvents.Clear();
            var answer = await call(Controller(Admin(), services));

            answer.Should().BeOfType(answerType, methodId);
            _fixture.AuditEvents.Should().ContainSingle(methodId).Which.Should().Match<CkMethodAuditRecord>(e =>
                e.MethodId == methodId && e.Transport == ArchiveMethodFallbackGuard.Transport);
        }

        (await SnapshotAsync(archive))!.Columns.Should().Contain(c => c.Name == "doubled").And.Contain(c => c.Path == "Current");
        var reformulated = await ColumnAsync(active, "fixed");
        (reformulated.PendingFormula ?? reformulated.Formula).Should().BeEquivalentTo("Voltage * 4");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private string Tenant => _fixture.GetSystemContext().TenantId;

    private ClaimsPrincipal User(params string[] roles) => UserOf(Tenant, roles);

    private ClaimsPrincipal Admin() => User(CommonConstants.StreamDataAdminRole);

    private static ClaimsPrincipal UserOf(string tokenTenant, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("sub", "65f0c0ffee65f0c0ffee9057"), new("scope", "octo_api openid"), new(ClaimTypes.Name, "archive-admin"),
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

    /// <summary>The request services with a facade that serves nothing: <c>Methods:StreamData:Enabled=false</c>.</summary>
    private IServiceProvider SwitchedOff()
    {
        var notHosted = A.Fake<IArchiveMethodFacade>();
        A.CallTo(() => notHosted.IsHosted).Returns(false);
        A.CallTo(() => notHosted.CanServe(A<ITenantContext>._, A<string>._, A<string>._)).Returns(false);
        A.CallTo(notHosted).Where(call => call.Method.Name == nameof(IArchiveMethodFacade.InvokeAsync))
            .Throws(new InvalidOperationException("the switched-off facade must not be invoked"));
        return new OverridingServiceProvider(_fixture.GetService<IServiceScopeFactory>().CreateScope().ServiceProvider,
            notHosted);
    }

    private async Task ExpectNoContentAsync(string methodId, Func<StreamDataController, Task<IActionResult>> call)
    {
        _fixture.AuditEvents.Clear();

        var answer = await call(Controller(Admin()));

        answer.Should().BeOfType<NoContentResult>(methodId);
        ExpectOneMethodEvent(methodId);
    }

    private async Task<RecomputeJobInfoRestDto> ExpectJobAsync(string methodId,
        Func<StreamDataController, Task<ActionResult<RecomputeJobInfoRestDto>>> call)
    {
        _fixture.AuditEvents.Clear();

        var answer = await call(Controller(Admin()));

        var job = answer.Result.Should().BeOfType<OkObjectResult>(methodId).Which.Value.Should()
            .BeOfType<RecomputeJobInfoRestDto>(methodId).Subject;
        job.RtId.Should().NotBeNullOrEmpty(methodId);
        ExpectOneMethodEvent(methodId);
        return job;
    }

    private void ExpectOneMethodEvent(string methodId) =>
        _fixture.AuditEvents.Should().ContainSingle(methodId).Which.Should().Match<CkMethodAuditRecord>(e =>
            e.MethodId == methodId && e.Outcome == CkMethodAuditOutcomes.Success && e.TenantId == Tenant &&
            e.Transport != ArchiveMethodFallbackGuard.Transport);

    /// <summary>Every column endpoint refused with 403 METHOD_FORBIDDEN (and the reason), one audit event each, nothing changed.</summary>
    private async Task AssertEveryEndpointRefusedAsync(ClaimsPrincipal caller, string? expectedReason, IServiceProvider? services,
        string? expectedTransport = null)
    {
        var archive = await CreateArchiveWithActiveComputedColumnAsync("RefCols", "kept");
        var failed = await CreateArchiveWithFailedComputedColumnAsync("RefRetry", "broken");
        var before = (await SnapshotAsync(archive))!.Columns;
        var jobsBefore = await JobCountAsync(archive, failed);
        var calls = new (string Name, Func<StreamDataController, Task<IActionResult>> Call)[]
        {
            ("addComputed", c => c.AddComputedColumn(Tenant, $"{archive}", "x", "Voltage", FormulaResultType.Double)),
            ("addColumn", c => c.AddArchiveColumn(Tenant, $"{archive}", "Current")),
            ("remove", c => c.RemoveComputedColumn(Tenant, $"{archive}", "kept")),
            ("update", c => c.UpdateComputedColumnFormula(Tenant, $"{archive}", "kept", "Voltage * 9")),
            ("retry", async c => (await c.RetryComputedColumnBackfill(Tenant, $"{failed}", "broken")).Result!),
            ("repair", async c => (await c.RepairComputedColumn(Tenant, $"{archive}", "kept", null, null)).Result!)
        };

        foreach (var (name, call) in calls)
        {
            _fixture.AuditEvents.Clear();
            var answer = await call(Controller(caller, services));

            var refused = answer.Should().BeOfType<ObjectResult>(name).Subject;
            refused.StatusCode.Should().Be(StatusCodes.Status403Forbidden, name);
            var problem = refused.Value.Should().BeOfType<ProblemDetails>(name).Subject;
            problem.Extensions["code"].Should().Be(CkMethodErrorCodes.MethodForbidden, name);
            if (expectedReason != null)
            {
                ((IReadOnlyDictionary<string, string>)problem.Extensions["parameters"]!)["reason"].Should()
                    .Be(expectedReason, name);
            }

            var audit = _fixture.AuditEvents.Should().ContainSingle(name).Subject;
            audit.Outcome.Should().Be(CkMethodErrorCodes.MethodForbidden, name);
            if (expectedTransport != null)
            {
                audit.Transport.Should().Be(expectedTransport, name);
            }
        }

        (await SnapshotAsync(archive))!.Columns.Should().BeEquivalentTo(before);
        (await ColumnAsync(failed, "broken")).ComputedState.Should().Be(ComputedColumnState.Failed);
        (await JobCountAsync(archive, failed)).Should().Be(jobsBefore, "no backfill or repair job was queued");
    }

    private async Task<int> JobCountAsync(params OctoObjectId[] archives)
    {
        var store = (await TenantAsync()).GetRecomputeJobStore()!;
        var count = 0;
        foreach (var archive in archives)
        {
            count += (await store.GetForArchiveAsync(archive, 1_000)).Count;
        }

        return count;
    }

    private async Task<ITenantContext> TenantAsync()
    {
        var systemContext = _fixture.GetSystemContext();
        return await systemContext.FindTenantContextAsync(systemContext.TenantId);
    }

    private async Task<ArchiveSnapshot?> SnapshotAsync(OctoObjectId rtId) =>
        await (await TenantAsync()).GetArchiveRuntimeStore().GetAsync(rtId);

    private async Task<CkArchiveColumnSpec> ColumnAsync(OctoObjectId rtId, string name) =>
        (await SnapshotAsync(rtId))!.Columns.Single(c => c.Name == name);

    private Task<OctoObjectId> CreateRawArchiveAsync(string namePrefix, bool activate) =>
        CreateArchiveOfAsync(_fixture.TestCkTypeId, "Voltage", namePrefix, activate);

    /// <summary>A raw archive of a CK type of the test model with one ingested column.</summary>
    private async Task<OctoObjectId> CreateArchiveOfAsync(string ckTypeId, string path, string namePrefix, bool activate = true)
    {
        var archive = new RtRawArchive
        {
            RtWellKnownName = $"{namePrefix}{Guid.NewGuid():N}",
            TargetCkTypeId = ckTypeId,
            Status = RtCkArchiveStatusEnum.Created,
            Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
            {
                new() { Path = path, Indexed = false, Required = false }
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

    /// <summary>An activated raw archive with a computed column that the backfill drain has made Active.</summary>
    private async Task<OctoObjectId> CreateArchiveWithActiveComputedColumnAsync(string prefix, string column)
    {
        var archive = await CreateRawArchiveAsync(prefix, activate: true);
        var lifecycle = (await TenantAsync()).GetArchiveLifecycleService()!;
        await lifecycle.EnqueueAddComputedColumnAsync(archive, column, "Voltage * 2", FormulaResultType.Double, indexed: false);
        await DrainUntilAsync(archive, column, ComputedColumnState.Active);
        return archive;
    }

    /// <summary>An activated raw archive with a computed column whose backfill ended Failed (state written as the drain does).</summary>
    private async Task<OctoObjectId> CreateArchiveWithFailedComputedColumnAsync(string prefix, string column)
    {
        var archive = await CreateArchiveWithActiveComputedColumnAsync(prefix, column);
        await (await TenantAsync()).GetArchiveRuntimeStore()
            .SetComputedColumnStateAsync(archive, column, ComputedColumnState.Failed);
        return archive;
    }

    private async Task DrainUntilAsync(OctoObjectId archive, string column, ComputedColumnState state)
    {
        var lifecycle = (await TenantAsync()).GetArchiveLifecycleService()!;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await lifecycle.RunPendingComputedColumnBackfillsAsync(TestContext.Current.CancellationToken);
            if ((await ColumnAsync(archive, column)).ComputedState == state)
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        (await ColumnAsync(archive, column)).ComputedState.Should().Be(state, "the backfill drain should have settled the column");
    }

    /// <summary>The request services with another <see cref="IArchiveMethodFacade" /> (the switch-off case).</summary>
    private sealed class OverridingServiceProvider(IServiceProvider inner, IArchiveMethodFacade facade) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IArchiveMethodFacade) ? facade : inner.GetService(serviceType);
    }
}
