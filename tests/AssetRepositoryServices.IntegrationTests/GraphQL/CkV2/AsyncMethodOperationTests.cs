using System.Security.Claims;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Operations;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.CkV2;

[CollectionDefinition(Name)]
public class AsyncMethodOperationCollection : ICollectionFixture<AsyncMethodGraphQlTestFixture>
{
    public const string Name = "AsyncMethodOperation";
}

/// <summary>
///     AB#8444 (F3.6): an asynchronous method mutation returns the operation handle, and <c>runtime.operations</c> reads
///     state, progress and the result or error of the operation for its owner and tenant administrators - behind the real
///     schema, gateway, in-process handler host and the MongoDB operation store of AB#8443 in the system database. Foreign,
///     unknown, expired and other-tenant ids are the same <c>OPERATION_NOT_FOUND</c>.
/// </summary>
[Collection(AsyncMethodOperationCollection.Name)]
public class AsyncMethodOperationTests
{
    private const string Field = "assetRepositoryIntegrationTestAsyncOperationThings";
    private const string OwnerSub = "65f0c0ffee65f0c0ffee6501";
    private const string OtherSub = "65f0c0ffee65f0c0ffee6502";

    private const string OperationSelection =
        "operationId methodId invocationId state progress { percent stageCode parameters { name value } } result " +
        "error { code parameters { name value } details { code } } acceptedAtUtc startedAtUtc completedAtUtc expiresAtUtc";

    private readonly AsyncMethodGraphQlTestFixture _fixture;

    public AsyncMethodOperationTests(AsyncMethodGraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
        _fixture.Observation.Reset();
    }

    private static ClaimsPrincipal Caller(string sub = OwnerSub, params string[] extraRoles) => new(new ClaimsIdentity(
        new[] { new Claim("sub", sub), new Claim("scope", "octo_api openid"), new Claim("role", "AccessTestAdmin") }
            .Concat(extraRoles.Select(r => new Claim("role", r))), "test"));

    /// <summary>A tenant administrator who did not start the operation (no method role needed to read).</summary>
    private static ClaimsPrincipal TenantAdministrator() => new(new ClaimsIdentity(
        [new Claim("sub", "65f0c0ffee65f0c0ffee6509"), new Claim("scope", "octo_api"), new Claim("role", "AdminPanelManagement")],
        "test"));

    private async Task<JObject> ExecuteAsync(string document, ClaimsPrincipal user, bool expectErrors = false)
    {
        var result = await _fixture.ExecuteGraphQlAsync(document, user: user);
        var json = _fixture.SerializeGraphQl(result);
        if (!expectErrors)
        {
            result.Errors.Should().BeNullOrEmpty(json);
        }

        return JObject.Parse(json);
    }

    private async Task<JObject> ExportAsync(string label, ClaimsPrincipal? user = null, string? key = null)
    {
        var keyArgument = key == null ? string.Empty : $", idempotencyKey: \"{key}\"";
        var json = await ExecuteAsync($$"""
            mutation { runtime { {{Field}} {
              export(input: { label: "{{label}}" }{{keyArgument}}) {
                invocationId operation { operationId acceptedAtUtc retryAfterSeconds expiresAtUtc }
              }
            } } }
            """, user ?? Caller());
        return (JObject)json.SelectToken($"data.runtime.{Field}.export")!;
    }

    private Task<JObject> ReadAsync(string operationId, ClaimsPrincipal user, bool expectErrors = false) =>
        ExecuteAsync($$"""
            query { runtime { operations(operationId: "{{operationId}}") { totalCount items { {{OperationSelection}} } } } }
            """, user, expectErrors);

    private async Task<JObject> WaitForFinalAsync(string operationId, ClaimsPrincipal user)
    {
        for (var i = 0; i < 100; i++)
        {
            var json = await ReadAsync(operationId, user);
            var item = (JObject)json.SelectToken("data.runtime.operations.items[0]")!;
            if (item.Value<string>("state") is "SUCCEEDED" or "FAILED")
            {
                return item;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Operation {operationId} did not end.");
    }

    private static JToken SingleError(JObject json) => json.SelectToken("errors")!.Should().ContainSingle().Subject;

    [Fact]
    public async Task AsyncMutation_ReturnsTheHandleWithTheFourWireFields()
    {
        var export = await ExportAsync("a");

        export.Value<string>("invocationId").Should().NotBeNullOrEmpty();
        var handle = (JObject)export["operation"]!;
        handle.Value<string>("operationId").Should().NotBeNullOrEmpty();
        handle["acceptedAtUtc"]!.Type.Should().NotBe(JTokenType.Null);
        handle.Value<int>("retryAfterSeconds").Should().BeGreaterThan(0);
        handle.Properties().Select(p => p.Name).Should()
            .BeEquivalentTo("operationId", "acceptedAtUtc", "retryAfterSeconds", "expiresAtUtc");
        export.ContainsKey("result").Should().BeFalse("the result of an asynchronous method is read through runtime.operations");
    }

    [Fact]
    public async Task Owner_ReadsStateAndResult_AndATenantAdministratorReadsTheSame()
    {
        var label = $"b{Guid.NewGuid():N}"[..12];
        var handle = (await ExportAsync(label))["operation"]!;
        var operationId = handle.Value<string>("operationId")!;

        var item = await WaitForFinalAsync(operationId, Caller());

        item.Value<string>("state").Should().Be("SUCCEEDED");
        item.Value<string>("result").Should().Be($"export-{label}.zip");
        item.Value<string>("methodId").Should().Be(AsyncMethodGraphQlTestFixture.MethodId);
        item["error"]!.Type.Should().Be(JTokenType.Null);
        item["expiresAtUtc"]!.Type.Should().NotBe(JTokenType.Null, "a final operation expires after the retention");
        item["completedAtUtc"]!.Type.Should().NotBe(JTokenType.Null);
        _fixture.Observation.WorksOf(label).Should().Be(1);

        var asAdministrator = await ReadAsync(operationId, TenantAdministrator());
        asAdministrator.SelectToken("data.runtime.operations.items[0]")!.ToString()
            .Should().Be(item.ToString(), "a tenant administrator reads exactly what the owner reads");
    }

    [Fact]
    public async Task RunningOperation_ShowsProgress_ThenFailureWithTheDeclaredCode()
    {
        var holdId = (await ExportAsync("hold"))["operation"]!.Value<string>("operationId")!;

        JObject? running = null;
        for (var i = 0; i < 100 && running == null; i++)
        {
            var item = (JObject)(await ReadAsync(holdId, Caller())).SelectToken("data.runtime.operations.items[0]")!;
            if (item.SelectToken("progress.percent")?.Type == JTokenType.Integer)
            {
                running = item;
            }
            else
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }

        running.Should().NotBeNull();
        running!.Value<string>("state").Should().Be("RUNNING");
        running.SelectToken("progress.percent")!.Value<int>().Should().Be(40);
        running.SelectToken("progress.stageCode")!.Value<string>().Should().Be("EXPORTING");
        running.SelectToken("progress.parameters")!.Select(p => p.Value<string>("name")).Should().Equal("done", "total");
        running["expiresAtUtc"]!.Type.Should().Be(JTokenType.Null, "an open operation has no expiry yet");
        _fixture.Observation.Gate.TrySetResult();
        (await WaitForFinalAsync(holdId, Caller())).Value<string>("state").Should().Be("SUCCEEDED");

        var failId = (await ExportAsync("fail"))["operation"]!.Value<string>("operationId")!;
        var failed = await WaitForFinalAsync(failId, Caller());
        failed.Value<string>("state").Should().Be("FAILED");
        failed.SelectToken("error.code")!.Value<string>().Should().Be(AsyncMethodGraphQlTestFixture.DeclaredError);
        failed.SelectToken("error.parameters")!.Should().ContainSingle(p => p.Value<string>("name") == "stage" &&
                                                                         p.Value<string>("value") == "COPY");
        failed["result"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public async Task AnotherUser_AnUnknownId_AnExpiredId_AndAnotherTenantsId_AllGetTheSameNotFound()
    {
        var operationId = (await ExportAsync("c"))["operation"]!.Value<string>("operationId")!;
        await WaitForFinalAsync(operationId, Caller());

        // an expired record of the caller (the store hides it before housekeeping removes it)
        var store = _fixture.OperationStore;
        var expiredId = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        await store.CreateAsync(Record(_fixture.SystemTenantId, expiredId, OwnerSub, now.AddDays(-9)),
            TestContext.Current.CancellationToken);
        (await store.CompleteAsync(_fixture.SystemTenantId, expiredId, new CkMethodOperationCompletion
        {
            State = CkMethodOperationState.Succeeded, CompletedAtUtc = now.AddDays(-8), ExpiresAtUtc = now.AddDays(-1)
        }, TestContext.Current.CancellationToken)).Should().BeTrue();

        // a record of another tenant (same owner, readable there) - cross-tenant through the same system database
        var otherTenantId = Guid.NewGuid().ToString("N");
        await store.CreateAsync(Record("othertenant-8444", otherTenantId, OwnerSub, now),
            TestContext.Current.CancellationToken);

        var answers = new[]
        {
            await ReadAsync(operationId, Caller(OtherSub), expectErrors: true),
            await ReadAsync("unknown-operation", Caller(), expectErrors: true),
            await ReadAsync(expiredId, Caller(), expectErrors: true),
            await ReadAsync(otherTenantId, Caller(), expectErrors: true),
            await ReadAsync(otherTenantId, TenantAdministrator(), expectErrors: true)
        };

        foreach (var answer in answers)
        {
            answer.SelectToken("data.runtime.operations")!.Type.Should().Be(JTokenType.Null);
            var error = SingleError(answer);
            error.SelectToken("extensions.code")!.Value<string>().Should().Be("OPERATION_NOT_FOUND");
        }

        // identical shape for every case: message, code and parameters (path aside)
        answers.Select(a =>
        {
            var error = (JObject)SingleError(a).DeepClone();
            error.Remove("locations");
            return error.ToString();
        }).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task OperationOfOneTenant_IsNotFoundFromAnotherTenantsRoute()
    {
        var operationId = (await ExportAsync("d"))["operation"]!.Value<string>("operationId")!;
        await WaitForFinalAsync(operationId, Caller());
        var otherTenant = await _fixture.GetTestTenantContextAsync();

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            query { runtime { operations(operationId: "{{operationId}}") { totalCount items { operationId } } } }
            """, user: Caller(), configure: o => o.UserContext = new GraphQlUserContext(Caller(), otherTenant));

        result.Errors.Should().ContainSingle().Which.Code.Should().Be("OPERATION_NOT_FOUND");
    }

    [Fact]
    public async Task Caller_ListsOwnOperations_FilteredByMethodAndState_AdministratorListsAllOfTheTenant()
    {
        var since = DateTime.UtcNow.AddSeconds(-1).ToString("O");
        var mine = (await ExportAsync("e", Caller(OwnerSub)))["operation"]!.Value<string>("operationId")!;
        var theirs = (await ExportAsync("f", Caller(OtherSub)))["operation"]!.Value<string>("operationId")!;
        await WaitForFinalAsync(mine, Caller(OwnerSub));
        await WaitForFinalAsync(theirs, Caller(OtherSub));

        var own = await ExecuteAsync($$"""
            query { runtime { operations(methodId: "{{AsyncMethodGraphQlTestFixture.MethodId}}", state: SUCCEEDED,
              since: "{{since}}") { totalCount items { operationId state } } } }
            """, Caller(OwnerSub));
        var ownIds = own.SelectToken("data.runtime.operations.items")!.Select(i => i.Value<string>("operationId"));
        ownIds.Should().Contain(mine).And.NotContain(theirs);

        var failedOnly = await ExecuteAsync("""
            query { runtime { operations(state: FAILED, take: 200) { items { state } } } }
            """, Caller(OwnerSub));
        failedOnly.SelectToken("data.runtime.operations.items")!.Select(i => i.Value<string>("state"))
            .Should().OnlyContain(s => s == "FAILED");

        var all = await ExecuteAsync($$"""
            query { runtime { operations(allOwners: true, since: "{{since}}", take: 200) { totalCount items { operationId } } } }
            """, TenantAdministrator());
        all.SelectToken("data.runtime.operations.items")!.Select(i => i.Value<string>("operationId"))
            .Should().Contain([mine, theirs]);

        var refused = await ExecuteAsync("""
            query { runtime { operations(allOwners: true) { totalCount } } }
            """, Caller(OwnerSub), expectErrors: true);
        SingleError(refused).SelectToken("extensions.code")!.Value<string>().Should().Be("OPERATION_QUERY_REFUSED");
        SingleError(refused).SelectToken("extensions.parameters.reason")!.Value<string>().Should().Be("administrators-only");
    }

    [Fact]
    public async Task SameIdempotencyKeyTwice_ReturnsTheSameOperationId_AndOnlyOneOperationExists()
    {
        var key = $"key-{Guid.NewGuid():N}";
        var label = $"idem{Guid.NewGuid():N}"[..12];

        var first = (await ExportAsync(label, key: key))["operation"]!.Value<string>("operationId");
        var second = (await ExportAsync(label, key: key))["operation"]!.Value<string>("operationId");

        second.Should().Be(first);
        _fixture.Observation.Accepts.Should().Be(1, "the replay answers with the stored accepted envelope");
        await WaitForFinalAsync(first!, Caller());
        _fixture.Observation.WorksOf(label).Should().Be(1);

        var page = await _fixture.OperationStore.ListAsync(new CkMethodOperationQuery
        {
            TenantId = _fixture.SystemTenantId, Owner = new CkMethodOperationOwner(OwnerSub, null),
            MethodId = AsyncMethodGraphQlTestFixture.MethodId, SinceUtc = DateTime.UtcNow.AddMinutes(-5), Take = 200
        }, TestContext.Current.CancellationToken);
        page.Items.Count(r => r.OperationId == first).Should().Be(1);
        _fixture.Observation.Accepts.Should().Be(1);
    }

    [Fact]
    public async Task AsyncMethodCall_StillCountsAsTheOneMethodCallOfTheRequest()
    {
        // OD-2b / AB#7020: an asynchronous method field next to another mutation is refused before anything runs
        var result = await ExecuteAsync($$"""
            mutation { runtime { {{Field}} {
              export(input: { label: "x" }) { invocationId }
              create(entities: [{ name: "n" }]) { rtId }
            } } }
            """, Caller(), expectErrors: true);

        SingleError(result).SelectToken("extensions.code")!.Value<string>().Should().Be("ONE_METHOD_CALL_PER_REQUEST");
        _fixture.Observation.Accepts.Should().Be(0);
    }

    [Fact]
    public async Task Schema_AsyncResultTypeHasTheHandle_AndNoResultField()
    {
        var json = await ExecuteAsync("""
            { handle: __type(name: "CkMethodOperationHandle") { fields { name type { kind name ofType { name } } } }
              result: __type(name: "AssetRepositoryIntegrationTestAsyncOperationThingExportMethodResult") { fields { name type { kind ofType { name } } } } }
            """, Caller());

        json.SelectToken("data.handle.fields")!.Select(f => f.Value<string>("name")).Should()
            .Equal("operationId", "acceptedAtUtc", "retryAfterSeconds", "expiresAtUtc");
        var resultFields = json.SelectToken("data.result.fields")!.ToDictionary(f => f.Value<string>("name")!);
        resultFields.Keys.Should().BeEquivalentTo("invocationId", "operation");
        resultFields["operation"].SelectToken("type.kind")!.Value<string>().Should().Be("NON_NULL");
        resultFields["operation"].SelectToken("type.ofType.name")!.Value<string>().Should().Be("CkMethodOperationHandle");
    }

    private static CkMethodOperationRecord Record(string tenantId, string operationId, string ownerSub, DateTime acceptedAt) =>
        new()
        {
            TenantId = CkMethodOperationTenant.Normalize(tenantId),
            OperationId = operationId,
            MethodId = AsyncMethodGraphQlTestFixture.MethodId,
            InvocationId = Guid.NewGuid(),
            Owner = new CkMethodOperationOwner(ownerSub, null),
            HandlerInstanceId = "test",
            State = CkMethodOperationState.Accepted,
            AcceptedAtUtc = acceptedAt,
            HeartbeatAtUtc = acceptedAt
        };
}
