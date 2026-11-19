using System.Security.Claims;
using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
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
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Audit;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
///     the asynchronous <c>RollupArchive.Recompute-2</c> and <c>BackfillFromSource-2</c> against the real host
///     (MongoDB, CrateDB, gateway, invocation pipeline, operation runner and store). The method returns an operation handle at
///     once and the operation ends with the job; the REST endpoints <c>recompute</c> and <c>backfill-from-source</c> are their
///     facades and answer <see cref="RecomputeJobInfoRestDto" /> as before; the job is visible in recompute-jobs and in the
///     operation record; a caller that goes away does not stop it; a token of another tenant and a caller without the role are
///     refused and change nothing.
/// </summary>
[Collection(ArchiveMethodFacadeCollection.Name)]
public class RecomputeMethodFacadeTests
{
    private static readonly DateTime From = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddDays(1);

    private readonly ArchiveMethodFacadeFixture _fixture;

    public RecomputeMethodFacadeTests(ArchiveMethodFacadeFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    [Fact]
    public async Task RestRecompute_AnswersTheJobAsBefore_AndTheJobIsInRecomputeJobs_AndInTheOperationRecord()
    {
        var rollup = await CreateRollupAsync("RecRest", activate: true);
        _fixture.AuditEvents.Clear();

        var answer = await Controller(Admin()).RecomputeArchive(Tenant, $"{rollup}", From, To, null);

        var job = answer.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<RecomputeJobInfoRestDto>()
            .Subject;
        job.State.Should().Be(RecomputeJobState.Completed.ToString());
        _fixture.AuditEvents.Should().ContainSingle().Which.Should().Match<CkMethodAuditRecord>(e =>
            e.MethodId == SystemStreamDataCkIds.RollupArchiveRecompute2MethodId && e.Outcome == CkMethodAuditOutcomes.Accepted);

        var listed = await Controller(Admin()).ListRecomputeJobsForArchive(Tenant, $"{rollup}");
        listed.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should()
            .BeAssignableTo<IEnumerable<RecomputeJobInfoRestDto>>().Which.Should().ContainSingle(j => j.RtId == job.RtId);

        var operation = await OperationOfJobAsync(SystemStreamDataCkIds.RollupArchiveRecompute2MethodId, job.RtId);
        operation.State.Should().Be(CkMethodOperationState.Succeeded);
        operation.StartedAtUtc.Should().NotBeNull("the operation ran (RUNNING) before it succeeded");

        // the direct call (a tenant without Recompute-2, or the switch off) answers the same type
        var direct = await Controller(Admin(), NotServing()).RecomputeArchive(Tenant,
            $"{await CreateRollupAsync("RecDirect", activate: true)}", From, To, null);
        direct.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<RecomputeJobInfoRestDto>();
    }

    [Fact]
    public async Task RestRecompute_OfAnInactiveRollup_AnswersTheFailedJobAsBefore_AndTheOperationFailedWithItsId()
    {
        var rollup = await CreateRollupAsync("RecInactive");

        var answer = await Controller(Admin()).RecomputeArchive(Tenant, $"{rollup}", From, To, null);

        var job = answer.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<RecomputeJobInfoRestDto>()
            .Subject;
        job.State.Should().Be(RecomputeJobState.Failed.ToString());
        var operation = await OperationOfJobAsync(SystemStreamDataCkIds.RollupArchiveRecompute2MethodId, job.RtId);
        operation.State.Should().Be(CkMethodOperationState.Failed);
        operation.Error!.Code.Should().Be(ArchiveMethodErrors.RecomputeFailed);
    }

    [Fact]
    public async Task Recompute2_ReturnsTheHandleAtOnce_AndTheOperationEndsWithTheJob_AlthoughTheCallerWentAway()
    {
        var rollup = await CreateRollupAsync("RecHandle", activate: true);
        using var caller = new CancellationTokenSource();

        var response = await _fixture.GetService<IArchiveMethodFacade>().InvokeAsync(await TenantAsync(),
            ArchiveMethodFacade.RollupArchiveCkTypeId, SystemStreamDataCkIds.RollupArchiveRecompute2MethodId, rollup,
            new RollupArchiveRecompute2Parameters { From = From, To = To }, Admin(), null, caller.Token);
        await caller.CancelAsync(); // the client disconnects right after the accept 

        response.IsAccepted.Should().BeTrue(response.ToString());
        response.ResultJson.Should().BeNull("the job is read through the operation, not the answer");
        var ended = await WaitForEndAsync(response.Operation!.OperationId);
        ended.State.Should().Be(CkMethodOperationState.Succeeded);
        var jobRtId = RecomputeOperationWait.JobRtIdOf(ended);
        jobRtId.Should().NotBeNull();
        var job = await (await TenantAsync()).GetRecomputeJobStore()!.GetAsync(jobRtId!.Value);
        job!.State.Should().Be(RecomputeJobState.Completed);
        job.ArchiveRtId.Should().Be(rollup);
        job.Trigger.Should().Be(RecomputeTrigger.Manual);
    }

    [Fact]
    public async Task RestBackfill_OfAnEmptySource_Is204AsBefore_AndTheOperationSucceededWithoutAJob()
    {
        var rollup = await CreateRollupAsync("BackEmpty", activate: true);
        _fixture.AuditEvents.Clear();

        var answer = await Controller(Admin()).BackfillRollupFromSource(Tenant, $"{rollup}");

        answer.Result.Should().BeOfType<NoContentResult>();
        var audit = _fixture.AuditEvents.Should().ContainSingle().Subject;
        audit.MethodId.Should().Be(SystemStreamDataCkIds.RollupArchiveBackfillFromSource2MethodId);
        audit.Outcome.Should().Be(CkMethodAuditOutcomes.Accepted);
    }

    [Fact]
    public async Task ATokenOfAnotherTenant_AndACallerWithoutTheRole_AreRefused_AndNoJobIsStarted()
    {
        var rollup = await CreateRollupAsync("RecRefused", activate: true);
        var callers = new (string Name, ClaimsPrincipal User, string? Reason)[]
        {
            ("foreign tenant", UserOf("some-other-tenant", CommonConstants.StreamDataAdminRole),
                CkMethodTokenReasons.TenantNotAllowed),
            ("without role", User("SomeOtherRole"), null)
        };

        foreach (var (name, user, reason) in callers)
        {
            foreach (var (endpoint, result) in new[]
                     {
                         ("recompute", (await Controller(user).RecomputeArchive(Tenant, $"{rollup}", From, To, null)).Result),
                         ("backfill", (await Controller(user).BackfillRollupFromSource(Tenant, $"{rollup}")).Result)
                     })
            {
                var refused = result.Should().BeOfType<ObjectResult>($"{name} {endpoint}").Subject;
                refused.StatusCode.Should().Be(StatusCodes.Status403Forbidden, $"{name} {endpoint}");
                var problem = refused.Value.Should().BeOfType<ProblemDetails>().Subject;
                problem.Extensions["code"].Should().Be(CkMethodErrorCodes.MethodForbidden, $"{name} {endpoint}");
                if (reason != null)
                {
                    ((IReadOnlyDictionary<string, string>)problem.Extensions["parameters"]!)["reason"].Should().Be(reason);
                }
            }
        }

        var jobs = await (await TenantAsync()).GetRecomputeJobStore()!.GetForArchiveAsync(rollup, 10);
        jobs.Should().BeEmpty("a refused call starts no recompute and queues no backfill");
        var rollupState = await (await TenantAsync()).GetRollupArchiveRuntimeStore()!.GetAsync(rollup);
        rollupState!.Status.Should().Be(CkArchiveStatus.Activated, "nothing changed on the rollup");
    }

    [Fact]
    public async Task RestBackfill_OfASourceWithData_AnswersThePendingJobAsBefore_AndTheOperationFollowsItToTheEnd()
    {
        var (source, rollup) = await CreateRollupWithSourceAsync("BackData");
        var write = await Controller(Admin()).Insert(Tenant, $"{source}", Bind($$"""
            [{ "rtId": "{{OctoObjectId.GenerateNewId()}}", "ckTypeId": "{{_fixture.TestCkTypeId}}",
               "timestamp": "{{From.AddHours(1):O}}", "attributes": { "Voltage": 230.0 } }]
            """));
        write.Should().BeOfType<OkObjectResult>();
        await new MultiSourceArchiveBuilder(_fixture).RefreshAsync(source); // CrateDB: make the row visible to the source scan

        var answer = await Controller(Admin()).BackfillRollupFromSource(Tenant, $"{rollup}");

        var job = answer.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<RecomputeJobInfoRestDto>()
            .Subject;
        job.State.Should().BeOneOf(RecomputeJobState.Pending.ToString(), RecomputeJobState.Running.ToString(),
            RecomputeJobState.Swapping.ToString(), RecomputeJobState.Completed.ToString());
        var queued = await OperationOfJobAsync(SystemStreamDataCkIds.RollupArchiveBackfillFromSource2MethodId, job.RtId);

        // the background orchestrator drains the queued range; the operation follows the job until it ended
        await (await TenantAsync()).GetRecomputeOrchestrator()!.TickAsync(TestContext.Current.CancellationToken);
        var ended = await WaitForEndAsync(queued.OperationId);
        ended.State.Should().Be(CkMethodOperationState.Succeeded);
        RecomputeOperationWait.JobRtIdOf(ended).ToString().Should().Be(job.RtId);
        var finished = await (await TenantAsync()).GetRecomputeJobStore()!.GetAsync(new OctoObjectId(job.RtId));
        finished!.State.Should().Be(RecomputeJobState.Completed);
    }

    [Fact]
    public async Task ADeletedOrNonRollupTarget_Is400NotFound_NoJobIsPersisted_AndNoOperationIsAccepted()
    {
        var raw = await CreateRawArchiveAsync("NotARollup");
        var missing = OctoObjectId.GenerateNewId();
        var store = _fixture.GetService<ICkMethodOperationStore>();
        // the fixture is shared by the whole collection: an earlier test's recompute can have been accepted moments
        // ago, so compare the operations before and after the refused calls instead of trusting a time window
        var since = DateTime.UtcNow.AddMinutes(-10);
        var before = (await ListRecomputeOperationsAsync(store, since)).Select(o => o.OperationId).ToHashSet();

        foreach (var target in new[] { raw, missing })
        {
            var recompute = await Controller(Admin()).RecomputeArchive(Tenant, $"{target}", From, To, null);
            var backfill = await Controller(Admin()).BackfillRollupFromSource(Tenant, $"{target}");

            // a behaviour change on an error path: the direct call answered 200 with a Failed job
            recompute.Result.Should().BeOfType<BadRequestObjectResult>($"recompute {target}").Which.Value.Should()
                .BeOfType<string>().Which.Should().Contain("does not exist");
            backfill.Result.Should().BeOfType<BadRequestObjectResult>($"backfill {target}");
            (await (await TenantAsync()).GetRecomputeJobStore()!.GetForArchiveAsync(target, 10)).Should()
                .BeEmpty("the gateway refuses the call before any job exists");
        }

        before.Should().HaveCountLessThan(CkMethodOperationQuery.MaxTake, "the comparison needs the complete list");
        (await ListRecomputeOperationsAsync(store, since)).Where(o => !before.Contains(o.OperationId)).Should()
            .BeEmpty("a refused call is never accepted as an operation");
    }

    private async Task<IReadOnlyList<CkMethodOperationRecord>> ListRecomputeOperationsAsync(ICkMethodOperationStore store,
        DateTime since) =>
        (await store.ListAsync(new CkMethodOperationQuery
        {
            TenantId = Tenant,
            MethodIds = [SystemStreamDataCkIds.RollupArchiveRecompute2MethodId, SystemStreamDataCkIds.RollupArchiveBackfillFromSource2MethodId],
            SinceUtc = since, Take = CkMethodOperationQuery.MaxTake
        }, TestContext.Current.CancellationToken)).Items;

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions MvcBodyOptions = new JsonOptions().JsonSerializerOptions;

    private static IReadOnlyList<InsertRawPointRestDto> Bind(string body) =>
        JsonSerializer.Deserialize<IReadOnlyList<InsertRawPointRestDto>>(body, MvcBodyOptions)
        ?? throw new InvalidOperationException("The body deserialized to null.");

    private string Tenant => _fixture.GetSystemContext().TenantId;

    private ClaimsPrincipal User(params string[] roles) => UserOf(Tenant, roles);

    private ClaimsPrincipal Admin() => User(CommonConstants.StreamDataAdminRole);

    private static ClaimsPrincipal UserOf(string tokenTenant, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("sub", "65f0c0ffee65f0c0ffee9056"), new("scope", "octo_api openid"), new(ClaimTypes.Name, "archive-admin"),
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

    /// <summary>Request services whose facade never serves (the direct call of a tenant without the method).</summary>
    private IServiceProvider NotServing()
    {
        var facade = A.Fake<IArchiveMethodFacade>();
        A.CallTo(() => facade.CanServe(A<ITenantContext>._, A<string>._, A<string>._)).Returns(false);
        return new OverridingServiceProvider(_fixture.GetService<IServiceScopeFactory>().CreateScope().ServiceProvider, facade);
    }

    private async Task<CkMethodOperationRecord> OperationOfJobAsync(string methodId, string jobRtId)
    {
        var page = await _fixture.GetService<ICkMethodOperationStore>().ListAsync(new CkMethodOperationQuery
        {
            TenantId = Tenant, MethodId = methodId, SinceUtc = DateTime.UtcNow.AddMinutes(-5), Take = 200
        }, TestContext.Current.CancellationToken);
        return page.Items.Should().ContainSingle(o => RecomputeOperationWait.JobRtIdOf(o).ToString() == jobRtId).Subject;
    }

    private async Task<CkMethodOperationRecord> WaitForEndAsync(string operationId)
    {
        var store = _fixture.GetService<ICkMethodOperationStore>();
        for (var i = 0; i < 300; i++)
        {
            if (await store.GetAsync(Tenant, operationId, TestContext.Current.CancellationToken) is { IsFinal: true } record)
            {
                return record;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Operation {operationId} did not end.");
    }

    private async Task<ITenantContext> TenantAsync()
    {
        var systemContext = _fixture.GetSystemContext();
        return await systemContext.FindTenantContextAsync(systemContext.TenantId);
    }

    private async Task<OctoObjectId> CreateRawArchiveAsync(string namePrefix)
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

        await (await TenantAsync()).GetArchiveLifecycleService()!.ActivateAsync(archive.RtId);
        return archive.RtId;
    }

    /// <summary>An hourly rollup of a fresh, activated raw source (empty).</summary>
    private async Task<OctoObjectId> CreateRollupAsync(string prefix, bool activate = false) =>
        (await CreateRollupWithSourceAsync(prefix, activate)).Rollup;

    private async Task<(OctoObjectId Source, OctoObjectId Rollup)> CreateRollupWithSourceAsync(string prefix,
        bool activate = true)
    {
        var source = await CreateRawArchiveAsync($"{prefix}Src");
        var tenant = await TenantAsync();
        var rollup = await tenant.GetRollupArchiveLifecycleService()!.CreateAsync($"{prefix}{Guid.NewGuid():N}",
            [new RollupSourceReference(source)], TimeSpan.FromHours(1), TimeSpan.Zero,
            [new CkRollupAggregationSpec("Voltage", CkRollupFunction.Sum, "voltageSum", null)],
            BucketAlignment.FixedSize, null, null, null);
        if (activate)
        {
            await tenant.GetArchiveLifecycleService()!.ActivateAsync(rollup);
        }

        return (source, rollup);
    }

    private sealed class OverridingServiceProvider(IServiceProvider inner, IArchiveMethodFacade facade) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IArchiveMethodFacade) ? facade : inner.GetService(serviceType);
    }
}
