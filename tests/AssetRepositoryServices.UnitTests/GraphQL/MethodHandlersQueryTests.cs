using System.Security.Claims;
using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.MethodHandlers;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Methods;
using Meshmakers.Octo.Services.Methods;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     AB#7837 (F3.2): the <c>methodHandlers</c> diagnostics query. System administrators only
///     (<c>SystemAdministratorGuard</c>), per method the instances with state, source, last seen and the persisted
///     snapshot time, the three method states, service data only.
/// </summary>
public class MethodHandlersQueryTests
{
    private const string SystemTenantId = "octosystem";
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    private const string AllFields = """
        methodHandlers {
          answeredBy { dispatcherInstanceId serviceVersion snapshotEnabled generatedAtUtc snapshotLoadedAtUtc
                       snapshotRowsRestored snapshotLoadFailed lastFlushUtc lastFlushFailed }
          methods {
            methodId transport state liveInstanceCount lastSeenUtc
            instances { serviceName instanceId version contractVersion tenants lastSeenUtc expiresUtc state source
                        persistedLastSeenUtc }
          }
        }
        """;

    private readonly ICkMethodHandlerRegistryDiagnostics _diagnostics = A.Fake<ICkMethodHandlerRegistryDiagnostics>();
    private readonly ICkMethodHandlerSnapshotStore _store = A.Fake<ICkMethodHandlerSnapshotStore>();
    private readonly ICkCacheService _cache = A.Fake<ICkCacheService>();
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private ICkMethodHandlerSnapshotStore? _registeredStore;

    public MethodHandlersQueryTests()
    {
        _registeredStore = _store;
        A.CallTo(() => _systemContext.TenantId).Returns(SystemTenantId);
        A.CallTo(() => _cache.IsTenantLoaded(SystemTenantId)).Returns(true);
        A.CallTo(() => _cache.GetCkTypes(SystemTenantId)).Returns([]);
        A.CallTo(() => _store.LoadAsync(A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<CkMethodHandlerSnapshotRecord>>([]));
        SetDiagnostics();
    }

    private static CkMethodHandlerDiagnosticsEntry Entry(string methodId, string instanceId, CkMethodHandlerState state,
        CkMethodHandlerEntrySource source = CkMethodHandlerEntrySource.Announcement, string service = "identity",
        DateTime? lastSeen = null) =>
        new(methodId, service, instanceId, "1.2.3", 1, lastSeen ?? Now.AddSeconds(-5), Now.AddSeconds(40), ["*"], source,
            state, CkMethodTransportNames.Bus);

    private void SetDiagnostics(IReadOnlyList<CkMethodHandlerDiagnosticsEntry>? entries = null,
        IReadOnlyList<string>? inProcess = null, CkMethodHandlerSnapshotDiagnostics? snapshot = null)
    {
        A.CallTo(() => _diagnostics.GetDiagnostics()).Returns(new CkMethodHandlerRegistryDiagnostics("dispatcher-1", Now,
            snapshot ?? new CkMethodHandlerSnapshotDiagnostics(true,
                new CkMethodHandlerSnapshotLoadStatus(Now.AddMinutes(-10), 4, 3, false, null),
                new CkMethodHandlerSnapshotFlushStatus(Now.AddSeconds(-20), false, null, null)),
            entries ?? [],
            (inProcess ?? []).Select(m => new CkMethodInProcessDiagnosticsEntry(m, CkMethodTransportNames.InProcess))
            .ToList()));
    }

    private static CkTypeGraph TypeWithMethods(params (string MethodId, string? Handler)[] methods) =>
        new(new CkId<CkTypeId>("System.Identity/User-1"), new CkCompiledTypeDto
        {
            TypeId = new CkTypeId("User-1"),
            Methods = methods.Select(m => new CkMethodDto
            {
                MethodId = m.MethodId,
                Execution = new CkMethodExecutionDto { Handler = m.Handler }
            }).ToList()
        });

    private static ClaimsPrincipal Token(string? tenantId, string? subject, params string[] roles)
    {
        var claims = new List<Claim>();
        if (tenantId != null)
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }

        if (subject != null)
        {
            claims.Add(new Claim("sub", subject));
        }

        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static ClaimsPrincipal SystemAdmin() =>
        Token(SystemTenantId, "admin", CommonConstants.AdminPanelManagementRole);

    private async Task<ExecutionResult> ExecuteAsync(ClaimsPrincipal? user, string selection = AllFields)
    {
        var query = new ObjectGraphType { Name = "Query" };
        MethodHandlersQuery.AddTo(query);
        var services = new ServiceCollection()
            .AddSingleton(_systemContext)
            .AddSingleton(_diagnostics)
            .AddSingleton(_cache)
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        if (_registeredStore != null)
        {
            services.AddSingleton(_registeredStore);
        }

        return await new DocumentExecuter().ExecuteAsync(o =>
        {
            o.Schema = new Schema { Query = query };
            o.Query = "{ " + selection + " }";
            o.RequestServices = services.BuildServiceProvider();
            o.UserContext = new GraphQlUserContext(user, A.Fake<ITenantContext>());
        });
    }

    private static JsonElement Data(ExecutionResult result)
    {
        result.Errors.Should().BeNull();
        return JsonDocument.Parse(new GraphQLSerializer().Serialize(result)).RootElement.GetProperty("data")
            .GetProperty("methodHandlers").Clone();
    }

    private static JsonElement Method(JsonElement data, string methodId) =>
        data.GetProperty("methods").EnumerateArray().Single(m => m.GetProperty("methodId").GetString() == methodId);

    [Fact]
    public async Task SystemTenantUserWithRole_ListsMethodsWithInstancesStateSourceAndTimes()
    {
        SetDiagnostics([
            Entry("System.Identity/User.Reset-1", "i-2", CkMethodHandlerState.Expired, service: "identity", lastSeen: Now.AddMinutes(-3)),
            Entry("System.Identity/User.Reset-1", "i-1", CkMethodHandlerState.Live)
        ]);
        A.CallTo(() => _store.LoadAsync(A<CancellationToken>._)).Returns(Task.FromResult<IReadOnlyList<CkMethodHandlerSnapshotRecord>>([
            new CkMethodHandlerSnapshotRecord { MethodId = "System.Identity/User.Reset-1", InstanceId = "i-1", LastSeenUtc = Now.AddSeconds(-15) }
        ]));

        var data = Data(await ExecuteAsync(SystemAdmin()));

        var answeredBy = data.GetProperty("answeredBy");
        answeredBy.GetProperty("dispatcherInstanceId").GetString().Should().Be("dispatcher-1");
        answeredBy.GetProperty("serviceVersion").GetString().Should().NotBeNullOrEmpty();
        answeredBy.GetProperty("snapshotEnabled").GetBoolean().Should().BeTrue();
        answeredBy.GetProperty("snapshotRowsRestored").GetInt32().Should().Be(3);
        answeredBy.GetProperty("lastFlushFailed").GetBoolean().Should().BeFalse();

        var method = Method(data, "System.Identity/User.Reset-1");
        method.GetProperty("transport").GetString().Should().Be("BUS");
        method.GetProperty("state").GetString().Should().Be("LIVE");
        method.GetProperty("liveInstanceCount").GetInt32().Should().Be(1);
        method.GetProperty("lastSeenUtc").GetDateTime().Should().Be(Now.AddSeconds(-5));
        var instances = method.GetProperty("instances").EnumerateArray().ToList();
        instances.Should().HaveCount(2);
        var live = instances.Single(i => i.GetProperty("instanceId").GetString() == "i-1");
        live.GetProperty("serviceName").GetString().Should().Be("identity");
        live.GetProperty("version").GetString().Should().Be("1.2.3");
        live.GetProperty("contractVersion").GetInt32().Should().Be(1);
        live.GetProperty("tenants").EnumerateArray().Select(t => t.GetString()).Should().Equal("*");
        live.GetProperty("state").GetString().Should().Be("LIVE");
        live.GetProperty("source").GetString().Should().Be("HEARTBEAT");
        live.GetProperty("expiresUtc").GetDateTime().Should().Be(Now.AddSeconds(40));
        live.GetProperty("persistedLastSeenUtc").GetDateTime().Should().Be(Now.AddSeconds(-15));
        var expired = instances.Single(i => i.GetProperty("instanceId").GetString() == "i-2");
        expired.GetProperty("state").GetString().Should().Be("EXPIRED");
        expired.GetProperty("persistedLastSeenUtc").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ClientCredentialsTokenOfTheSystemTenant_Answers()
    {
        SetDiagnostics([Entry("M.T.A-1", "i-1", CkMethodHandlerState.Live)]);

        var data = Data(await ExecuteAsync(Token(SystemTenantId, subject: null)));

        data.GetProperty("methods").GetArrayLength().Should().Be(1);
    }

    [Theory]
    [InlineData("customer-admin")]
    [InlineData("system-user-without-role")]
    [InlineData("system-client-wrong-tenant")]
    [InlineData("unauthenticated")]
    [InlineData("no-tenant-claim")]
    public async Task OtherCallers_AreForbiddenAndNoHandlerDataIsRead(string caller)
    {
        SetDiagnostics([Entry("M.T.A-1", "secret-instance", CkMethodHandlerState.Live, service: "secret-service")]);
        ClaimsPrincipal? user = caller switch
        {
            "customer-admin" => Token("customer", "admin", CommonConstants.AdminPanelManagementRole,
                CommonConstants.TenantManagementRole),
            "system-user-without-role" => Token(SystemTenantId, "user"),
            "system-client-wrong-tenant" => Token("customer", subject: null),
            "no-tenant-claim" => Token(null, "admin", CommonConstants.AdminPanelManagementRole),
            _ => null
        };

        var result = await ExecuteAsync(user);

        result.Errors.Should().ContainSingle().Which.Code.Should().Be("MethodHandlersForbidden");
        var json = new GraphQLSerializer().Serialize(result);
        json.Should().NotContain("secret-instance").And.NotContain("secret-service").And.NotContain("dispatcher-1");
        JsonDocument.Parse(json).RootElement.GetProperty("data").GetProperty("methodHandlers").ValueKind
            .Should().Be(JsonValueKind.Null);
        A.CallTo(() => _diagnostics.GetDiagnostics()).MustNotHaveHappened();
        A.CallTo(() => _store.LoadAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task MethodStates_NotAnnounced_NoLiveHandler_InProcess_Live()
    {
        A.CallTo(() => _cache.GetCkTypes(SystemTenantId)).Returns([
            TypeWithMethods(("Defined", null), ("DefinedService", "service"), ("PipelineBacked", "pipeline"),
                ("Local", "service"))
        ]);
        SetDiagnostics([
            Entry("System.Identity/User.Gone-1", "i-1", CkMethodHandlerState.Expired),
            Entry("System.Identity/User.Gone-1", "i-2", CkMethodHandlerState.Withdrawn),
            Entry("System.Identity/User.Up-1", "i-3", CkMethodHandlerState.Live)
        ], inProcess: ["System.Identity/User.Local"]);

        var data = Data(await ExecuteAsync(SystemAdmin(), "methodHandlers { methods { methodId transport state liveInstanceCount lastSeenUtc instances { instanceId } } }"));

        var ids = data.GetProperty("methods").EnumerateArray().Select(m => m.GetProperty("methodId").GetString()).ToList();
        ids.Should().Contain(["System.Identity/User.Defined", "System.Identity/User.DefinedService",
            "System.Identity/User.Gone-1", "System.Identity/User.Up-1", "System.Identity/User.Local"]);
        ids.Should().NotContain(i => i!.Contains("PipelineBacked"), "a pipeline-implemented method expects no service handler");

        var notAnnounced = Method(data, "System.Identity/User.Defined");
        notAnnounced.GetProperty("state").GetString().Should().Be("NOT_ANNOUNCED");
        notAnnounced.GetProperty("liveInstanceCount").GetInt32().Should().Be(0);
        notAnnounced.GetProperty("lastSeenUtc").ValueKind.Should().Be(JsonValueKind.Null);
        notAnnounced.GetProperty("instances").GetArrayLength().Should().Be(0);

        var gone = Method(data, "System.Identity/User.Gone-1");
        gone.GetProperty("state").GetString().Should().Be("NO_LIVE_HANDLER");
        gone.GetProperty("liveInstanceCount").GetInt32().Should().Be(0);
        gone.GetProperty("instances").GetArrayLength().Should().Be(2);

        Method(data, "System.Identity/User.Up-1").GetProperty("state").GetString().Should().Be("LIVE");

        var local = Method(data, "System.Identity/User.Local");
        local.GetProperty("transport").GetString().Should().Be("IN_PROCESS");
        local.GetProperty("state").GetString().Should().Be("LIVE");
    }

    [Fact]
    public async Task RestoredRowIsSnapshot_AnnouncedRowIsHeartbeat()
    {
        SetDiagnostics([Entry("M.T.A-1", "i-1", CkMethodHandlerState.Expired, CkMethodHandlerEntrySource.Snapshot)]);
        var before = Data(await ExecuteAsync(SystemAdmin()));
        Method(before, "M.T.A-1").GetProperty("instances")[0].GetProperty("source").GetString().Should().Be("SNAPSHOT");

        SetDiagnostics([Entry("M.T.A-1", "i-1", CkMethodHandlerState.Live)]);
        var after = Data(await ExecuteAsync(SystemAdmin()));
        var instance = Method(after, "M.T.A-1").GetProperty("instances")[0];
        instance.GetProperty("source").GetString().Should().Be("HEARTBEAT");
        instance.GetProperty("state").GetString().Should().Be("LIVE");
    }

    [Fact]
    public async Task FailingSnapshotStore_StillAnswers_WithoutLeakingTheException()
    {
        SetDiagnostics([Entry("M.T.A-1", "i-1", CkMethodHandlerState.Live)], snapshot:
            new CkMethodHandlerSnapshotDiagnostics(true, null,
                new CkMethodHandlerSnapshotFlushStatus(null, true, Now, "MongoException")));
        A.CallTo(() => _store.LoadAsync(A<CancellationToken>._))
            .Throws(new InvalidOperationException("mongodb://user:pw@host connection refused"));

        var result = await ExecuteAsync(SystemAdmin());

        var data = Data(result);
        Method(data, "M.T.A-1").GetProperty("instances")[0].GetProperty("persistedLastSeenUtc").ValueKind
            .Should().Be(JsonValueKind.Null);
        data.GetProperty("answeredBy").GetProperty("lastFlushFailed").GetBoolean().Should().BeTrue();
        new GraphQLSerializer().Serialize(result).Should().NotContain("mongodb://").And.NotContain("connection refused");
    }

    [Fact]
    public async Task MissingSnapshotStore_StillAnswers_AndSnapshotIsDisabled()
    {
        _registeredStore = null;
        SetDiagnostics([Entry("M.T.A-1", "i-1", CkMethodHandlerState.Live)],
            snapshot: new CkMethodHandlerSnapshotDiagnostics(false, null,
                new CkMethodHandlerSnapshotFlushStatus(null, false, null, null)));

        var data = Data(await ExecuteAsync(SystemAdmin()));

        data.GetProperty("answeredBy").GetProperty("snapshotEnabled").GetBoolean().Should().BeFalse();
        data.GetProperty("answeredBy").GetProperty("snapshotLoadedAtUtc").ValueKind.Should().Be(JsonValueKind.Null);
        Method(data, "M.T.A-1").GetProperty("instances")[0].GetProperty("persistedLastSeenUtc").ValueKind
            .Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task SearchAndStatesFilters_NarrowTheMethodList()
    {
        SetDiagnostics([
            Entry("System.Identity/User.Reset-1", "i-1", CkMethodHandlerState.Live, service: "identity"),
            Entry("System.Bot/Job.Run-1", "i-2", CkMethodHandlerState.Expired, service: "bot-services")
        ]);

        var byText = Data(await ExecuteAsync(SystemAdmin(), "methodHandlers { methods(search: \"RESET\") { methodId } }"));
        byText.GetProperty("methods").EnumerateArray().Select(m => m.GetProperty("methodId").GetString())
            .Should().Equal("System.Identity/User.Reset-1");

        var byService = Data(await ExecuteAsync(SystemAdmin(), "methodHandlers { methods(search: \"bot-services\") { methodId } }"));
        byService.GetProperty("methods").GetArrayLength().Should().Be(1);

        var byState = Data(await ExecuteAsync(SystemAdmin(), "methodHandlers { methods(states: [NO_LIVE_HANDLER]) { methodId state } }"));
        byState.GetProperty("methods").EnumerateArray().Select(m => m.GetProperty("methodId").GetString())
            .Should().Equal("System.Bot/Job.Run-1");
    }

    [Fact]
    public void GraphTypes_HoldServiceDataOnly()
    {
        static IEnumerable<string> Names(IComplexGraphType t) => t.Fields.Select(f => f.Name);

        Names(new MethodHandlersAnsweredByGraphType()).Should().BeEquivalentTo("dispatcherInstanceId", "serviceVersion",
            "snapshotEnabled", "generatedAtUtc", "snapshotLoadedAtUtc", "snapshotRowsRestored", "snapshotLoadFailed",
            "lastFlushUtc", "lastFlushFailed");
        Names(new MethodHandlerMethodGraphType()).Should().BeEquivalentTo("methodId", "transport", "state",
            "liveInstanceCount", "lastSeenUtc", "instances");
        Names(new MethodHandlerInstanceGraphType()).Should().BeEquivalentTo("serviceName", "instanceId", "version",
            "contractVersion", "tenants", "lastSeenUtc", "expiresUtc", "state", "source", "persistedLastSeenUtc");
        Names(new MethodHandlersQuery()).Should().BeEquivalentTo("answeredBy", "methods");
        new MethodHandlerMethodStateGraphType().Values.Select(v => v.Name).Should()
            .Equal("LIVE", "NO_LIVE_HANDLER", "NOT_ANNOUNCED");
        new MethodHandlerInstanceStateGraphType().Values.Select(v => v.Name).Should()
            .Equal("LIVE", "EXPIRED", "WITHDRAWN", "NO_QUEUE_BOUND");
        new MethodHandlerSourceGraphType().Values.Select(v => v.Name).Should().Equal("HEARTBEAT", "SNAPSHOT");
        new MethodHandlerTransportGraphType().Values.Select(v => v.Name).Should().Equal("BUS", "IN_PROCESS");
    }
}
