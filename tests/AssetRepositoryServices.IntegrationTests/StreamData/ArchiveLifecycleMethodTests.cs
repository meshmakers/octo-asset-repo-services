using System.Security.Claims;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
///     AB#8598 (F4.3): the archive lifecycle as CK methods of System.StreamData, end to end through the real GraphQL schema,
///     gateway, invocation pipeline and engine (<see cref="ArchiveMethodStreamDataFixture" />). Per method: the same archive
///     transition as the streamData mutation it replaces (parity, run side by side on two archives), the declared role
///     (METHOD_FORBIDDEN without StreamDataAdmin, nothing changed), the mutation's error code for a transition that the
///     current status does not allow, and no reach into another tenant. The mutations keep working (their existing tests
///     stay green) and carry the method id as deprecation reason.
/// </summary>
[Collection(ArchiveMethodCollection.Name)]
public partial class ArchiveLifecycleMethodTests
{
    private const string Raw = "systemStreamDataRawArchives";
    private const string TimeRange = "systemStreamDataTimeRangeArchives";
    private const string Rollup = "systemStreamDataRollupArchives";

    private readonly ArchiveMethodStreamDataFixture _fixture;

    public ArchiveLifecycleMethodTests(ArchiveMethodStreamDataFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    // ── principals ──────────────────────────────────────────────────────────────────────────────

    private ClaimsPrincipal User(params string[] roles) => UserOf(_fixture.GetSystemContext().TenantId, roles);

    private static ClaimsPrincipal UserOf(string tokenTenant, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("sub", "65f0c0ffee65f0c0ffee8598"), new("scope", "octo_api openid"), new(ClaimTypes.Name, "archive-admin"),
            new("tenant_id", tokenTenant)
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim("role", role));
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "IntegrationTests"));
    }

    private ClaimsPrincipal Admin() => User(CommonConstants.StreamDataAdminRole);

    private ClaimsPrincipal WithoutRole() => User("SomeOtherRole");

    // ── the method table ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryReplacedMutation_HasExactlyOneMethod_AndItsDeprecationReasonNamesIt()
    {
        ArchiveMethodHost.Handlers.Select(h => h.Mutation).Should().BeEquivalentTo(
            "activateArchive", "disableArchive", "enableArchive", "retryArchiveActivation", "deleteArchive",
            "createTimeRangeArchive", "createRollupArchive",
            // AB#8599
            "freezeRollupArchive", "unfreezeRollupArchive", "rewindRollupWatermark", "recomputeArchive", "backfillRollupFromSource");
        ArchiveMethodHost.DeprecationReasonOf("activateArchive")
            .Should().Be("Use System.StreamData/Archive.Activate-1");
        ArchiveMethodHost.DeprecationReasonOf("createRollupArchive")
            .Should().Be("Use System.StreamData/RollupArchive.CreateArchive-1");

        var transport = _fixture.GetService<ICkInProcessMethodTransport>();
        foreach (var handler in ArchiveMethodHost.Handlers)
        {
            transport.CanHandle(handler.MethodId).Should().BeTrue(handler.MethodId);
        }
    }

    [Fact]
    public async Task TheSchema_StillHasEveryMutation_DeprecatedWithTheMethodId()
    {
        var result = await _fixture.ExecuteGraphQlAsync("""
            { __type(name: "StreamDataMutations") { fields(includeDeprecated: true) { name isDeprecated deprecationReason } } }
            """, user: Admin());
        result.Errors.Should().BeNullOrEmpty();
        var fields = JObject.Parse(_fixture.SerializeGraphQl(result)).SelectToken("data.__type.fields")!
            .ToDictionary(f => f.Value<string>("name")!, f => f);

        foreach (var handler in ArchiveMethodHost.Handlers)
        {
            fields.Should().ContainKey(handler.Mutation);
            fields[handler.Mutation].Value<bool>("isDeprecated").Should().BeTrue(handler.Mutation);
            fields[handler.Mutation].Value<string>("deprecationReason").Should().Be($"Use {handler.MethodId}");
        }

        // the column mutations are not part of these stories (AB#8600)
        fields["addComputedColumn"].Value<bool>("isDeprecated").Should().BeFalse();
    }

    // ── parity: same transition as the mutation ─────────────────────────────────────────────────

    [Fact]
    public async Task Activate_TransitionsLikeActivateArchive()
    {
        var viaMutation = await CreateRawArchiveAsync("ActMut");
        var viaMethod = await CreateRawArchiveAsync("ActMeth");

        var mutation = await MutationAsync("activateArchive", viaMutation);
        var method = await TransitionAsync(Raw, "activate", viaMethod);

        AssertTransitionResult(method, viaMethod, "Activate", "ACTIVATED");
        AssertTransitionResult(mutation, viaMutation, "Activate", "ACTIVATED");
        (await SnapshotAsync(viaMethod))!.Status.Should().Be(CkArchiveStatus.Activated);
        (await SnapshotAsync(viaMutation))!.Status.Should().Be(CkArchiveStatus.Activated);
    }

    [Fact]
    public async Task Disable_ThenEnable_TransitionLikeTheMutations()
    {
        var viaMutation = await CreateRawArchiveAsync("DisMut", activate: true);
        var viaMethod = await CreateRawArchiveAsync("DisMeth", activate: true);

        AssertTransitionResult(await MutationAsync("disableArchive", viaMutation), viaMutation, "Disable", "DISABLED");
        AssertTransitionResult(await TransitionAsync(Raw, "disable", viaMethod), viaMethod, "Disable", "DISABLED");
        (await SnapshotAsync(viaMethod))!.Status.Should().Be(CkArchiveStatus.Disabled);

        AssertTransitionResult(await MutationAsync("enableArchive", viaMutation), viaMutation, "Enable", "ACTIVATED");
        AssertTransitionResult(await TransitionAsync(Raw, "enable", viaMethod), viaMethod, "Enable", "ACTIVATED");
        (await SnapshotAsync(viaMethod))!.Status.Should().Be(CkArchiveStatus.Activated);
    }

    [Fact]
    public async Task RetryActivation_TransitionsAFailedArchiveLikeRetryArchiveActivation()
    {
        var viaMutation = await CreateRawArchiveAsync("RetryMut");
        var viaMethod = await CreateRawArchiveAsync("RetryMeth");
        var store = (await TenantAsync()).GetArchiveRuntimeStore();
        await store.SetStatusAsync(viaMutation, CkArchiveStatus.Failed);
        await store.SetStatusAsync(viaMethod, CkArchiveStatus.Failed);

        AssertTransitionResult(await MutationAsync("retryArchiveActivation", viaMutation), viaMutation, "RetryActivation",
            "ACTIVATED");
        AssertTransitionResult(await TransitionAsync(Raw, "retryActivation", viaMethod), viaMethod, "RetryActivation",
            "ACTIVATED");
        (await SnapshotAsync(viaMethod))!.Status.Should().Be(CkArchiveStatus.Activated);
    }

    [Fact]
    public async Task DeleteArchive_DeletesLikeDeleteArchive_AndAnswersTrue()
    {
        var viaMutation = await CreateRawArchiveAsync("DelMut", activate: true);
        var viaMethod = await CreateRawArchiveAsync("DelMeth", activate: true);

        var mutation = await _fixture.ExecuteGraphQlAsync(
            $$"""mutation { streamData { deleteArchive(rtId: "{{viaMutation}}") } }""", user: Admin());
        Data(mutation).SelectToken("streamData.deleteArchive")!.Value<bool>().Should().BeTrue();

        var method = await _fixture.ExecuteGraphQlAsync(
            $$"""mutation { runtime { {{Raw}} { deleteArchive(rtId: "{{viaMethod}}") { invocationId result } } } }""",
            user: Admin());
        Data(method).SelectToken($"runtime.{Raw}.deleteArchive.result")!.Value<bool>().Should().BeTrue();

        (await SnapshotAsync(viaMutation)).Should().BeNull();
        (await SnapshotAsync(viaMethod)).Should().BeNull("the method deletes the entity like the mutation");
    }

    [Fact]
    public async Task TimeRangeCreateArchive_CreatesLikeCreateTimeRangeArchive()
    {
        var mutation = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { streamData { createTimeRangeArchive(input: {
              rtWellKnownName: "TrMut{{Guid.NewGuid():N}}", targetCkTypeId: "{{_fixture.TestCkTypeId}}",
              columns: [{ path: "Voltage", indexed: true, required: false }], periodMs: 900000 }) } }
            """, user: Admin());
        var viaMutation = new OctoObjectId(Data(mutation).SelectToken("streamData.createTimeRangeArchive")!.Value<string>()!);

        var method = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{TimeRange}} { createArchive(input: { archive: {
              rtWellKnownName: "TrMeth{{Guid.NewGuid():N}}", targetCkTypeId: "{{_fixture.TestCkTypeId}}",
              columns: [{ path: "Voltage", indexed: true, required: false }], periodMs: 900000 } }) { result } } } }
            """, user: Admin());
        var viaMethod = new OctoObjectId(Data(method).SelectToken($"runtime.{TimeRange}.createArchive.result")!.Value<string>()!);

        var store = (await TenantAsync()).GetTimeRangeArchiveRuntimeStore()!;
        var expected = await store.GetAsync(viaMutation);
        var actual = await store.GetAsync(viaMethod);
        actual.Should().NotBeNull();
        actual!.Status.Should().Be(CkArchiveStatus.Created);
        actual.TargetCkTypeId.Should().Be(expected!.TargetCkTypeId);
        actual.Columns.Should().BeEquivalentTo(expected.Columns);
        actual.Period.Should().Be(expected.Period);
    }

    [Fact]
    public async Task RollupCreateArchive_CreatesLikeCreateRollupArchive()
    {
        var source = await CreateRawArchiveAsync("RollupSrc", activate: true);
        const string body = """
            bucketSizeMs: 3600000, watermarkLagMs: 0, bucketAlignment: CALENDAR_DAY, referenceTimeZone: "Europe/Vienna",
            aggregations: [{ sourcePath: "Voltage", function: SUM, targetColumnName: "voltageSum" }]
            """;

        var mutation = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { streamData { createRollupArchive(input: { rtWellKnownName: "RuMut{{Guid.NewGuid():N}}",
              sources: [{ sourceArchiveRtId: "{{source}}" }], {{body}} }) } }
            """, user: Admin());
        var viaMutation = new OctoObjectId(Data(mutation).SelectToken("streamData.createRollupArchive")!.Value<string>()!);

        var method = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Rollup}} { createArchive(input: { archive: { rtWellKnownName: "RuMeth{{Guid.NewGuid():N}}",
              sources: [{ sourceArchiveRtId: "{{source}}" }], {{body}} } }) { result } } } }
            """, user: Admin());
        var viaMethod = new OctoObjectId(Data(method).SelectToken($"runtime.{Rollup}.createArchive.result")!.Value<string>()!);

        var store = (await TenantAsync()).GetRollupArchiveRuntimeStore()!;
        var expected = await store.GetAsync(viaMutation);
        var actual = await store.GetAsync(viaMethod);
        actual.Should().NotBeNull();
        actual!.Status.Should().Be(CkArchiveStatus.Created);
        actual.Sources.Should().BeEquivalentTo(expected!.Sources);
        actual.BucketSize.Should().Be(expected.BucketSize);
        actual.BucketAlignment.Should().Be(expected.BucketAlignment).And.Be(BucketAlignment.CalendarDay);
        actual.ReferenceTimeZone.Should().Be(expected.ReferenceTimeZone);
        actual.Aggregations.Should().BeEquivalentTo(expected.Aggregations);
        actual.TargetCkTypeId.Should().Be(expected.TargetCkTypeId);
    }

    [Fact]
    public async Task RollupCreateArchive_WithNeitherSourceForm_IsAValidationFailure_AndCreatesNothing()
    {
        var method = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Rollup}} { createArchive(input: { archive: { rtWellKnownName: "RuNone{{Guid.NewGuid():N}}",
              bucketSizeMs: 3600000, watermarkLagMs: 0, aggregations: [{ sourcePath: "Voltage", function: SUM }] } }) { result } } } }
            """, user: Admin());

        var error = method.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(CkMethodErrorCodes.MethodValidationFailed);
    }

    // ── authorization, refusals ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryMethod_WithoutStreamDataAdmin_IsMethodForbidden_AndChangesNothing()
    {
        var created = await CreateRawArchiveAsync("Forbidden");
        var activated = await CreateRawArchiveAsync("ForbiddenAct", activate: true);
        var calls = new[]
        {
            $$"""{{Raw}} { activate(rtId: "{{created}}") { invocationId } }""",
            $$"""{{Raw}} { disable(rtId: "{{activated}}") { invocationId } }""",
            $$"""{{Raw}} { enable(rtId: "{{activated}}") { invocationId } }""",
            $$"""{{Raw}} { retryActivation(rtId: "{{created}}") { invocationId } }""",
            $$"""{{Raw}} { deleteArchive(rtId: "{{activated}}") { invocationId } }""",
            $$"""{{TimeRange}} { createArchive(input: { archive: { targetCkTypeId: "{{_fixture.TestCkTypeId}}", columns: [{ path: "Voltage", indexed: false, required: false }] } }) { invocationId } }""",
            $$"""{{Rollup}} { createArchive(input: { archive: { sources: [{ sourceArchiveRtId: "{{activated}}" }], bucketSizeMs: 3600000, watermarkLagMs: 0, aggregations: [{ sourcePath: "Voltage", function: SUM }] } }) { invocationId } }"""
        };

        foreach (var call in calls)
        {
            var result = await _fixture.ExecuteGraphQlAsync($"mutation {{ runtime {{ {call} }} }}", user: WithoutRole());
            result.Errors.Should().ContainSingle(call).Which.Code.Should().Be(CkMethodErrorCodes.MethodForbidden, call);
        }

        (await SnapshotAsync(created))!.Status.Should().Be(CkArchiveStatus.Created);
        (await SnapshotAsync(activated))!.Status.Should().Be(CkArchiveStatus.Activated);
    }

    [Fact]
    public async Task ATransitionTheStatusDoesNotAllow_AnswersTheMutationsCode()
    {
        var created = await CreateRawArchiveAsync("Invalid");

        var mutation = await MutationAsync("disableArchive", created, expectSuccess: false);
        var method = await TransitionAsync(Raw, "disable", created, expectSuccess: false);

        // the mutation adds GraphQL's non-null error after its own; its own error comes first
        var mutationError = mutation.Errors!.First();
        var methodError = method.Errors.Should().ContainSingle().Subject;
        mutationError.Code.Should().Be(Statics.GraphQlErrorStreamDataInvalidTransition);
        methodError.Code.Should().Be(mutationError.Code, "the method answers the mutation's code");
        var parameters = MethodErrorParameters(method);
        parameters.Value<string>("fromStatus").Should().Be("Created");
        parameters.Value<string>("archiveRtId").Should().Be(created.ToString());
        (await SnapshotAsync(created))!.Status.Should().Be(CkArchiveStatus.Created);
    }

    [Fact]
    public async Task AnArchiveOutsideTheCallersTenant_IsNotReachable()
    {
        var archive = await CreateRawArchiveAsync("Tenant");

        // an id that is not an archive of the call's tenant: the gateway refuses before the handler runs
        var foreign = OctoObjectId.GenerateNewId();
        var result = await TransitionAsync(Raw, "activate", foreign, expectSuccess: false);
        result.Errors.Should().ContainSingle().Which.Code.Should().Be(CkMethodErrorCodes.MethodTargetNotFound);

        // a caller in another tenant: its schema has no archive methods (System.StreamData is not installed there), so the
        // archive of the system tenant cannot be named at all
        var other = await _fixture.GetSystemContext().FindTenantContextAsync(_fixture.TestTenantId);
        var crossTenant = await _fixture.ExecuteGraphQlInTenantAsync(other,
            $$"""mutation { runtime { {{Raw}} { activate(rtId: "{{archive}}") { invocationId } } } }""", Admin());
        crossTenant.Errors.Should().NotBeNullOrEmpty();
        (await SnapshotAsync(archive))!.Status.Should().Be(CkArchiveStatus.Created);
    }

    [Fact]
    public async Task ATokenOfAnotherTenant_IsMethodForbidden_EvenWithTheRole_AndChangesNothing()
    {
        // The HTTP tenant gate may run in LogOnly (this fixture has no HTTP gate at all): the in-process transport refuses the
        // caller token of another tenant centrally (CkMethodCallerTenantFilter, AB#8711) before the handler runs.
        var archive = await CreateRawArchiveAsync("OtherTenant");
        var foreignUser = UserOf("some-other-tenant", CommonConstants.StreamDataAdminRole);
        var foreignService = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("client_id", "foreign-service"), new Claim("scope", "octo_api"), new Claim("tenant_id", "some-other-tenant"),
            new Claim("role", CommonConstants.StreamDataAdminRole), new Claim(ClaimTypes.Role, CommonConstants.StreamDataAdminRole)
        ], "IntegrationTests"));

        foreach (var caller in new[] { foreignUser, foreignService })
        {
            var result = await _fixture.ExecuteGraphQlAsync(
                $$"""mutation { runtime { {{Raw}} { activate(rtId: "{{archive}}") { invocationId } } } }""", user: caller);
            var error = result.Errors.Should().ContainSingle().Subject;
            error.Code.Should().Be(CkMethodErrorCodes.MethodForbidden);
            MethodErrorParameters(result).Value<string>("reason").Should().Be(CkMethodTokenReasons.TenantNotAllowed);
        }

        (await SnapshotAsync(archive))!.Status.Should().Be(CkArchiveStatus.Created);
    }

    [Fact]
    public async Task AServiceTokenOfTheCallsTenant_IsAllowed()
    {
        var archive = await CreateRawArchiveAsync("ServiceToken");
        var service = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("client_id", "archive-service"), new Claim("scope", "octo_api"),
            new Claim("tenant_id", _fixture.GetSystemContext().TenantId),
            new Claim("role", CommonConstants.StreamDataAdminRole), new Claim(ClaimTypes.Role, CommonConstants.StreamDataAdminRole)
        ], "IntegrationTests"));

        var result = await _fixture.ExecuteGraphQlAsync(
            $$"""mutation { runtime { {{Raw}} { activate(rtId: "{{archive}}") { invocationId } } } }""", user: service);

        result.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(result));
        (await SnapshotAsync(archive))!.Status.Should().Be(CkArchiveStatus.Activated);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private async Task<ExecutionResult> MutationAsync(string mutation, OctoObjectId rtId, bool expectSuccess = true)
    {
        var result = await _fixture.ExecuteGraphQlAsync(
            $$"""mutation { streamData { {{mutation}}(rtId: "{{rtId}}") { archiveRtId status transition } } }""", user: Admin());
        if (expectSuccess)
        {
            result.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(result));
        }

        return result;
    }

    private async Task<ExecutionResult> TransitionAsync(string typeField, string method, OctoObjectId rtId,
        bool expectSuccess = true)
    {
        var result = await _fixture.ExecuteGraphQlAsync(
            $$"""mutation { runtime { {{typeField}} { {{method}}(rtId: "{{rtId}}") { invocationId result { archiveRtId status transition } } } } }""",
            user: Admin());
        if (expectSuccess)
        {
            result.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(result));
        }

        return result;
    }

    private void AssertTransitionResult(ExecutionResult result, OctoObjectId rtId, string transition, string status)
    {
        var data = Data(result);
        var node = data.SelectToken("streamData")?.First?.First ??
                   data.SelectToken("runtime")!.First!.First!.First!.First!.SelectToken("result")!;
        node.Value<string>("archiveRtId").Should().Be(rtId.ToString());
        node.Value<string>("transition").Should().Be(transition);
        Normalize(node.Value<string>("status")!).Should().Be(Normalize(status));
    }

    private static string Normalize(string value) => value.Replace("_", "", StringComparison.Ordinal).ToUpperInvariant();

    private JObject Data(ExecutionResult result)
    {
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return (JObject)JObject.Parse(json)["data"]!;
    }

    private JToken MethodErrorParameters(ExecutionResult result) =>
        JObject.Parse(_fixture.SerializeGraphQl(result)).SelectToken("errors[0].extensions.parameters")!;

    private async Task<ITenantContext> TenantAsync()
    {
        var systemContext = _fixture.GetSystemContext();
        return await systemContext.FindTenantContextAsync(systemContext.TenantId);
    }

    private async Task<ArchiveSnapshot?> SnapshotAsync(OctoObjectId rtId) =>
        await (await TenantAsync()).GetArchiveRuntimeStore().GetAsync(rtId);

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
}
