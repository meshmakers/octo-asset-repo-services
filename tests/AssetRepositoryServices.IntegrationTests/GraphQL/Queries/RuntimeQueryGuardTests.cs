using System.Diagnostics.Metrics;
using System.Security.Claims;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Observability;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
///     AB#8898: the unbounded runtime-entity query guard at the GraphQL API boundary, per mode (Off / Warn / Enforce)
///     and per entry point (<c>runtime.&lt;CkType&gt;</c>, <c>runtime.runtimeEntities</c>, persisted query rows,
///     transient query rows). Bounded shapes (paged, key lookup within the limit, totalCount only, aggregation) run in
///     every mode; an in-process engine call without <c>first</c> is never refused, logged or counted.
/// </summary>
/// <remarks>
///     The asset repository has no REST endpoint that queries runtime entities (the REST exports are background jobs),
///     so the GraphQL entry points are the whole API boundary of this service; SDK, CLI and MCP reach it through them.
/// </remarks>
[Collection(PersistentQueryCollection.Name)]
public sealed class RuntimeQueryGuardTests : IDisposable
{
    private const string ClientId = "ab8898-integration";
    private const string CustomerCkTypeId = "AssetRepositoryIntegrationTest/Customer";
    private const string MeteringPointCkTypeId = "AssetRepositoryIntegrationTest/MeteringPoint";

    private readonly GraphQlTestFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly RuntimeQueryGuardOptions _options;
    private readonly RuntimeQueryGuardMode _previousMode;
    private readonly int _previousMaxPageSize;
    private readonly MeterListener _listener = new();
    private readonly List<Dictionary<string, object?>> _measurements = [];

    public RuntimeQueryGuardTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _fixture.OutputHelper = output;

        _options = fixture.Provider!.GetRequiredService<IOptionsMonitor<RuntimeQueryGuardOptions>>().CurrentValue;
        _previousMode = _options.Mode;
        _previousMaxPageSize = _options.MaxPageSize;

        // Only the instruments of this fixture's provider: other collections run in parallel with their own hosts.
        var meterFactory = fixture.Provider!.GetRequiredService<IMeterFactory>();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, meterFactory) &&
                instrument.Name == RuntimeQueryGuardMetrics.UnboundedMetricName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            lock (_measurements)
            {
                _measurements.Add(tags.ToArray().ToDictionary(t => t.Key, t => t.Value));
            }
        });
        _listener.Start();
    }

    public enum EntryPoint
    {
        TypedEntities,
        GenericEntities,
        PersistedQueryRows,
        TransientQueryRows
    }

    public static TheoryData<RuntimeQueryGuardMode, EntryPoint> Matrix()
    {
        var data = new TheoryData<RuntimeQueryGuardMode, EntryPoint>();
        foreach (var mode in Enum.GetValues<RuntimeQueryGuardMode>())
        foreach (var entryPoint in Enum.GetValues<EntryPoint>())
        {
            data.Add(mode, entryPoint);
        }

        return data;
    }

    public void Dispose()
    {
        _listener.Dispose();
        _options.Mode = _previousMode;
        _options.MaxPageSize = _previousMaxPageSize;
    }

    [Fact]
    public void Guard_IsRegisteredWithTheDefaultWarnModeAnd1000()
    {
        var options = _fixture.Provider!.GetRequiredService<IOptions<RuntimeQueryGuardOptions>>().Value;

        options.Mode.Should().Be(RuntimeQueryGuardMode.Warn);
        options.MaxPageSize.Should().Be(1000);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task UnboundedQuery_FollowsTheMode(RuntimeQueryGuardMode mode, EntryPoint entryPoint)
    {
        _options.Mode = mode;
        var (query, cleanup) = await UnboundedQueryAsync(entryPoint);
        try
        {
            var result = await _fixture.ExecuteGraphQlAsync(query, user: Principal());
            _output.WriteLine(_fixture.SerializeGraphQl(result));

            var expectedCkTypeId = entryPoint is EntryPoint.PersistedQueryRows or EntryPoint.TransientQueryRows
                ? MeteringPointCkTypeId
                : CustomerCkTypeId;

            if (mode == RuntimeQueryGuardMode.Enforce)
            {
                var error = result.Errors.Should().ContainSingle().Which;
                error.Code.Should().Be("ASSET1007");
                error.Message.Should().Contain(expectedCkTypeId).And.Contain("1000");
                error.Extensions.Should().ContainKey("errorId");
                _fixture.SerializeGraphQl(result).Should().NotContain("\"rtId\"", "nothing was loaded");
            }
            else
            {
                result.Errors.Should().BeNullOrEmpty($"the query runs in mode {mode}");
                _fixture.SerializeGraphQl(result).Should().Contain("\"rtId\"");
            }

            var counted = Measurements(expectedCkTypeId);
            if (mode == RuntimeQueryGuardMode.Off)
            {
                counted.Should().BeEmpty("Off neither logs nor counts");
            }
            else
            {
                counted.Should().ContainSingle().Which.Should()
                    .Contain(RuntimeQueryGuardMetrics.ModeLabel, mode == RuntimeQueryGuardMode.Enforce ? "enforce" : "warn");
            }
        }
        finally
        {
            await cleanup();
        }
    }

    [Theory]
    [InlineData("first: 1000", false)]
    [InlineData("first: 1001", true)]
    [InlineData("first: 2", false)]
    public async Task Enforce_RefusesOnlyAFirstAboveTheLimit(string paging, bool refused)
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;

        var result = await _fixture.ExecuteGraphQlAsync(
            $$"""{ runtime { assetRepositoryIntegrationTestCustomer({{paging}}) { items { rtId } } } }""",
            user: Principal());

        if (refused)
        {
            result.Errors.Should().ContainSingle().Which.Code.Should().Be("ASSET1007");
        }
        else
        {
            result.Errors.Should().BeNullOrEmpty();
        }
    }

    [Fact]
    public async Task Enforce_TotalCountOnlyAndAggregationRun()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;

        var count = await _fixture.ExecuteGraphQlAsync(
            "{ runtime { assetRepositoryIntegrationTestCustomer { totalCount } } }", user: Principal());
        var aggregation = await _fixture.ExecuteGraphQlAsync($$"""
            {
              runtime {
                transientQuery {
                  aggregation(
                    ckId: "{{MeteringPointCkTypeId}}"
                    columnPaths: [{ attributePath: "meterReading", aggregationType: COUNT }]
                  ) {
                    items { rows { items { ... on RtAggregationQueryRow { ckTypeId } } } }
                  }
                }
              }
            }
            """, user: Principal());

        count.Errors.Should().BeNullOrEmpty();
        _fixture.SerializeGraphQl(count).Should().MatchRegex("\"totalCount\":[1-9]");
        aggregation.Errors.Should().BeNullOrEmpty();
        Measurements(CustomerCkTypeId).Should().BeEmpty();
        Measurements(MeteringPointCkTypeId).Should().BeEmpty();
    }

    [Fact]
    public async Task Enforce_KeyLookupWithinTheLimitRunsAndAboveTheLimitIsRefused()
    {
        var rtIds = await CustomerRtIdsAsync(3);
        var keys = string.Join(", ", rtIds.Select(id => $"\"{id}\""));
        var lookup = $$"""{ runtime { assetRepositoryIntegrationTestCustomer(rtIds: [{{keys}}]) { items { rtId } } } }""";
        _options.Mode = RuntimeQueryGuardMode.Enforce;

        _options.MaxPageSize = 3;
        var within = await _fixture.ExecuteGraphQlAsync(lookup, user: Principal());

        _options.MaxPageSize = 2;
        var above = await _fixture.ExecuteGraphQlAsync(lookup, user: Principal());

        within.Errors.Should().BeNullOrEmpty("3 keys with MaxPageSize 3 are bounded");
        _fixture.SerializeGraphQl(within).Should().Contain(rtIds[0]);
        var error = above.Errors.Should().ContainSingle().Which;
        error.Code.Should().Be("ASSET1007");
        error.Message.Should().Contain("2");
        Measurements(CustomerCkTypeId).Should().ContainSingle("only the lookup above the limit is unbounded");
    }

    [Fact]
    public async Task Enforce_InternalEngineCallWithoutFirstIsNeitherRefusedNorCounted()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;
        _options.MaxPageSize = 1;

        // An in-process engine call (communication controller, background job, blueprint install, CK migration) uses
        // ITenantRepository directly, never the GraphQL entry points: no first, no take.
        var tenantRepository = _fixture.GetSystemContext().GetSystemTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var result = await tenantRepository.GetRtEntitiesByTypeAsync(session, CustomerCkTypeId,
            RtEntityQueryOptions.Create());
        await session.AbortTransactionAsync();

        result.Items.Should().HaveCountGreaterThan(1, "the internal call loads every customer, MaxPageSize 1 or not");
        Measurements(CustomerCkTypeId).Should().BeEmpty("internal calls are not counted");
    }

    private List<Dictionary<string, object?>> Measurements(string ckTypeId)
    {
        lock (_measurements)
        {
            return _measurements.Where(m => Equals(m[RuntimeQueryGuardMetrics.CkTypeLabel], ckTypeId) &&
                                            Equals(m[RuntimeQueryGuardMetrics.ClientIdLabel], ClientId)).ToList();
        }
    }

    private static ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity([new Claim("client_id", ClientId), new Claim("sub", "ab8898-subject")], "Bearer"));

    private async Task<List<string>> CustomerRtIdsAsync(int count)
    {
        var result = await _fixture.ExecuteGraphQlAsync(
            $$"""{ runtime { assetRepositoryIntegrationTestCustomer(first: {{count}}) { items { rtId } } } }""");
        result.Errors.Should().BeNullOrEmpty();
        var ids = JObject.Parse(_fixture.SerializeGraphQl(result))
            .SelectTokens("data.runtime.assetRepositoryIntegrationTestCustomer.items[*].rtId")
            .Select(t => t.Value<string>()!).ToList();
        ids.Should().HaveCount(count);
        return ids;
    }

    private async Task<(string Query, Func<Task> Cleanup)> UnboundedQueryAsync(EntryPoint entryPoint)
    {
        switch (entryPoint)
        {
            case EntryPoint.TypedEntities:
                return ("{ runtime { assetRepositoryIntegrationTestCustomer { items { rtId } } } }", NoCleanup);
            case EntryPoint.GenericEntities:
                return ($$"""
                    query GuardGeneric {
                      runtime { runtimeEntities(ckId: "{{CustomerCkTypeId}}") { edges { node { rtId } } } }
                    }
                    """, NoCleanup);
            case EntryPoint.TransientQueryRows:
                return ($$"""
                    {
                      runtime {
                        transientQuery {
                          simple(ckId: "{{MeteringPointCkTypeId}}", columnPaths: ["rtWellKnownName"]) {
                            items { rows { items { ... on RtSimpleQueryRow { rtId } } } }
                          }
                        }
                      }
                    }
                    """, NoCleanup);
            case EntryPoint.PersistedQueryRows:
                var queryRtId = await CreatePersistedQueryAsync();
                return ($$"""
                    {
                      runtime {
                        runtimeQuery(rtId: "{{queryRtId}}") {
                          items { rows { items { ... on RtSimpleQueryRow { rtId } } } }
                        }
                      }
                    }
                    """, () => DeletePersistedQueryAsync(queryRtId));
            default:
                throw new ArgumentOutOfRangeException(nameof(entryPoint), entryPoint, null);
        }
    }

    private static Task NoCleanup() => Task.CompletedTask;

    private async Task<string> CreatePersistedQueryAsync()
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation {
              runtime {
                systemSimpleRtQuerys {
                  create(entities: [{
                    name: "AB8898GuardQuery"
                    queryCkTypeId: "{{MeteringPointCkTypeId}}"
                    columns: ["rtWellKnownName"]
                  }]) { rtId }
                }
              }
            }
            """);
        result.Errors.Should().BeNullOrEmpty();
        return JObject.Parse(_fixture.SerializeGraphQl(result))
            .SelectToken("data.runtime.systemSimpleRtQuerys.create[0].rtId")!.Value<string>()!;
    }

    private async Task DeletePersistedQueryAsync(string queryRtId)
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation {
              runtime {
                runtimeEntities {
                  delete(entities: [{ rtId: "{{queryRtId}}", ckTypeId: "System/SimpleRtQuery" }])
                }
              }
            }
            """);
        result.Errors.Should().BeNullOrEmpty();
    }
}
