using System.Diagnostics.Metrics;
using System.Security.Claims;
using FluentAssertions;
using GraphQL;
using GraphQL.Execution;
using GraphQL.Types;
using GraphQL.Validation;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
///     Navigation connections inside an entity, against the asset repository's GraphQL schema and MongoDB.
///     <c>Car.children</c> (System/ParentChild inbound, multiplicity N) is a to-many navigation,
///     <c>VehicleReading.parent</c> (outbound, One) a to-one navigation. The sample data has two readings below car 1
///     and more cars, so one query resolves <c>children</c> for several parents.
/// </summary>
[Collection(PersistentQueryCollection.Name)]
public sealed class RuntimeQueryNestedNavigationGuardTests : IDisposable
{
    private const string ClientId = "nested-guard-integration";
    private const string ReadingCkTypeId = "AssetRepositoryIntegrationTest/VehicleReading";
    private const string VehicleCkTypeId = "AssetRepositoryIntegrationTest/Vehicle";
    private const string CarCkTypeId = "AssetRepositoryIntegrationTest/Car";
    private const string Reading1 = "67000005eeee5555ffff0001";
    private const string Car1 = "67000004dddd4444eeee0001";

    private const string ChildrenWithoutFirst = $$"""
        {
          runtime {
            assetRepositoryIntegrationTestCar(first: 10) {
              items {
                rtId
                children(ckTypeIds: ["{{ReadingCkTypeId}}"]) {
                  items { ... on AssetRepositoryIntegrationTestVehicleReading { rtId } }
                }
              }
            }
          }
        }
        """;

    private readonly GraphQlTestFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly RuntimeQueryGuardOptions _options;
    private readonly RuntimeQueryGuardMode _previousMode;
    private readonly int _previousMaxPageSize;
    private readonly MeterListener _listener = new();
    private readonly List<Dictionary<string, object?>> _measurements = [];

    public RuntimeQueryNestedNavigationGuardTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _fixture.OutputHelper = output;

        _options = fixture.Provider!.GetRequiredService<IOptionsMonitor<RuntimeQueryGuardOptions>>().CurrentValue;
        _previousMode = _options.Mode;
        _previousMaxPageSize = _options.MaxPageSize;

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

    public void Dispose()
    {
        _listener.Dispose();
        _options.Mode = _previousMode;
        _options.MaxPageSize = _previousMaxPageSize;
    }

    // --- To-many navigation: each mode ---

    [Theory]
    [InlineData(RuntimeQueryGuardMode.Off)]
    [InlineData(RuntimeQueryGuardMode.Warn)]
    [InlineData(RuntimeQueryGuardMode.Enforce)]
    public async Task ToManyNavigationWithoutFirst_FollowsTheMode(RuntimeQueryGuardMode mode)
    {
        _options.Mode = mode;

        var result = await ExecuteAsync(ChildrenWithoutFirst);
        var json = _fixture.SerializeGraphQl(result);

        if (mode == RuntimeQueryGuardMode.Enforce)
        {
            result.Errors.Should().NotBeNullOrEmpty();
            result.Errors!.Should().OnlyContain(e => e.Code == "ASSET1007");
            result.Errors!.Select(e => e.Extensions!["errorId"]).Distinct().Should().ContainSingle(
                "one decision per navigation and request, repeated for every parent");
            var error = result.Errors![0];
            error.Message.Should().Contain("children").And.Contain("1000");
            ((IDictionary<string, object?>)error.Extensions!["parameters"]!)["fieldPath"].Should()
                .Be("runtime.assetRepositoryIntegrationTestCar.items.children");
            json.Should().NotContain(Reading1, "no metering point was loaded");
            json.Should().Contain(Car1, "the paged parents themselves are read");
        }
        else
        {
            result.Errors.Should().BeNullOrEmpty($"the query runs in mode {mode}");
            json.Should().Contain(Reading1);
        }

        var counted = Measurements();
        if (mode == RuntimeQueryGuardMode.Off)
        {
            counted.Should().BeEmpty("Off neither logs nor counts");
        }
        else
        {
            counted.Should().ContainSingle("the navigation is counted once per request, not once per facility")
                .Which.Should().Contain(RuntimeQueryGuardMetrics.ModeLabel,
                    mode == RuntimeQueryGuardMode.Enforce ? "enforce" : "warn");
        }
    }

    [Theory]
    [InlineData("first: 1000", false)]
    [InlineData("first: 1001", true)]
    public async Task Enforce_ToManyRefusesOnlyAFirstAboveTheLimit(string paging, bool refused)
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;

        var result = await ExecuteAsync(Children(paging));

        if (refused)
        {
            result.Errors.Should().NotBeNullOrEmpty().And.OnlyContain(e => e.Code == "ASSET1007");
        }
        else
        {
            result.Errors.Should().BeNullOrEmpty();
            _fixture.SerializeGraphQl(result).Should().Contain(Reading1);
        }
    }

    [Fact]
    public async Task Enforce_ToManyTotalCountOnlyAndKeyLookupRun()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;

        var count = await ExecuteAsync($$"""
            {
              runtime {
                assetRepositoryIntegrationTestCar(first: 10) {
                  items { children(ckTypeIds: ["{{ReadingCkTypeId}}"]) { totalCount } }
                }
              }
            }
            """);
        var lookup = await ExecuteAsync(Children($"rtIds: [\"{Reading1}\"]"));

        count.Errors.Should().BeNullOrEmpty();
        _fixture.SerializeGraphQl(count).Should().MatchRegex("\"totalCount\":[1-9]");
        lookup.Errors.Should().BeNullOrEmpty();
        _fixture.SerializeGraphQl(lookup).Should().Contain(Reading1);
        Measurements().Should().BeEmpty();
    }

    [Theory]
    [InlineData("rtId: null")]
    [InlineData("rtIds: []")]
    public async Task Enforce_AnEmptyKeyLookupOnANavigationIsRefused(string lookup)
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;

        // Abuse case: the navigation resolver filters by keys only when it gets one, so this loads every target.
        var result = await ExecuteAsync(Children(lookup));

        result.Errors.Should().NotBeNullOrEmpty().And.OnlyContain(e => e.Code == "ASSET1007");
        _fixture.SerializeGraphQl(result).Should().NotContain(Reading1);
    }

    [Fact]
    public async Task Enforce_FirstFromAVariableAboveTheLimitIsRefused()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            query Kids($n: Int) {
              runtime {
                assetRepositoryIntegrationTestCar(first: 10) {
                  items {
                    children(first: $n, ckTypeIds: ["{{ReadingCkTypeId}}"]) {
                      items { ... on AssetRepositoryIntegrationTestVehicleReading { rtId } }
                    }
                  }
                }
              }
            }
            """, """{ "n": 5000 }""", Principal());

        result.Errors.Should().NotBeNullOrEmpty().And.OnlyContain(e => e.Code == "ASSET1007");
    }

    [Fact]
    public async Task Enforce_AliasesAndFragmentsDoNotHideTheNavigation()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;

        var result = await ExecuteAsync($$"""
            query Hidden {
              runtime {
                assetRepositoryIntegrationTestCar(first: 10) {
                  rows: items { ...Facility }
                }
              }
            }
            fragment Facility on AssetRepositoryIntegrationTestCar {
              ... on AssetRepositoryIntegrationTestCar {
                kids: children(ckTypeIds: ["{{ReadingCkTypeId}}"]) {
                  list: items { ... on AssetRepositoryIntegrationTestVehicleReading { rtId } }
                }
              }
            }
            """);

        result.Errors.Should().NotBeNullOrEmpty().And.OnlyContain(e => e.Code == "ASSET1007");
        _fixture.SerializeGraphQl(result).Should().NotContain(Reading1);
    }

    [Fact]
    public async Task Enforce_TwoNavigationsInOneQueryAreDecidedEachOnTheirOwn()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;

        var result = await ExecuteAsync($$"""
            {
              runtime {
                assetRepositoryIntegrationTestCar(first: 10) {
                  items {
                    paged: children(first: 5, ckTypeIds: ["{{ReadingCkTypeId}}"]) {
                      items { ... on AssetRepositoryIntegrationTestVehicleReading { rtId } }
                    }
                    all: children(ckTypeIds: ["{{ReadingCkTypeId}}"]) {
                      items { ... on AssetRepositoryIntegrationTestVehicleReading { rtId } }
                    }
                  }
                }
              }
            }
            """);

        result.Errors.Should().NotBeNullOrEmpty().And.OnlyContain(e => e.Code == "ASSET1007");
        result.Errors!.Should().OnlyContain(e => e.Path!.Last().Equals("all"));
        _fixture.SerializeGraphQl(result).Should().Contain(Reading1, "the paged alias runs");
    }

    // --- To-one navigation: exempt ---

    [Theory]
    [InlineData(RuntimeQueryGuardMode.Warn)]
    [InlineData(RuntimeQueryGuardMode.Enforce)]
    public async Task ToOneNavigationWithoutFirst_IsNotFlagged(RuntimeQueryGuardMode mode)
    {
        _options.Mode = mode;

        var result = await ExecuteAsync($$"""
            {
              runtime {
                assetRepositoryIntegrationTestVehicleReading(first: 10) {
                  items {
                    rtId
                    parent(ckTypeIds: ["{{VehicleCkTypeId}}"]) {
                      items { ... on AssetRepositoryIntegrationTestCar { rtId } }
                    }
                  }
                }
              }
            }
            """);

        result.Errors.Should().BeNullOrEmpty();
        _fixture.SerializeGraphQl(result).Should().Contain(Car1);
        Measurements().Should().BeEmpty();
    }

    [Fact]
    public async Task Enforce_NestedInsideNestedIsCheckedAtEveryLevel()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;

        // facility -> children (paged) -> parent (to-one) -> children (no first)
        var result = await ExecuteAsync($$"""
            {
              runtime {
                assetRepositoryIntegrationTestCar(first: 10) {
                  items {
                    children(first: 5, ckTypeIds: ["{{ReadingCkTypeId}}"]) {
                      items {
                        ... on AssetRepositoryIntegrationTestVehicleReading {
                          rtId
                          parent(ckTypeIds: ["{{VehicleCkTypeId}}"]) {
                            items {
                              ... on AssetRepositoryIntegrationTestCar {
                                children(ckTypeIds: ["{{ReadingCkTypeId}}"]) {
                                  items { ... on AssetRepositoryIntegrationTestVehicleReading { rtWellKnownName } }
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """);

        result.Errors.Should().NotBeNullOrEmpty().And.OnlyContain(e => e.Code == "ASSET1007");
        ((IDictionary<string, object?>)result.Errors![0].Extensions!["parameters"]!)["fieldPath"].Should().Be(
            "runtime.assetRepositoryIntegrationTestCar.items.children.items.parent.items.children");
        _fixture.SerializeGraphQl(result).Should().Contain(Reading1, "the paged first level runs")
            .And.NotContain("ReadingBMW320i_001", "the unbounded third level is not read");
    }

    // --- Generic navigations ---

    [Theory]
    [InlineData("INBOUND", "false", ReadingCkTypeId, CarCkTypeId, true)]
    [InlineData("OUTBOUND", "false", VehicleCkTypeId, ReadingCkTypeId, false)]
    [InlineData("OUTBOUND", "true", VehicleCkTypeId, ReadingCkTypeId, true)]
    public async Task Enforce_GenericTargetsFollowTheRoleMultiplicity(string direction, string indirect,
        string targetCkTypeId, string originCkTypeId, bool refused)
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;

        var result = await ExecuteAsync($$"""
            {
              runtime {
                runtimeEntities(ckId: "{{originCkTypeId}}", first: 10) {
                  items {
                    associations {
                      targets(roleId: "System/ParentChild", direction: {{direction}}, ckId: "{{targetCkTypeId}}",
                              includeIndirect: {{indirect}}) { items { rtId } }
                    }
                  }
                }
              }
            }
            """);

        if (refused)
        {
            result.Errors.Should().NotBeNullOrEmpty().And.OnlyContain(e => e.Code == "ASSET1007");
        }
        else
        {
            result.Errors.Should().BeNullOrEmpty("the outbound side of ParentChild (parent) is to-one");
        }
    }

    [Theory]
    [InlineData("roleId: \"System/ParentChild\", direction: INBOUND", true)]
    [InlineData("direction: INBOUND", true)]
    [InlineData("roleId: \"System/ParentChild\", direction: INBOUND, first: 10", false)]
    public async Task Enforce_GenericDefinitionsFollowTheSameRules(string arguments, bool refused)
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;

        var result = await ExecuteAsync($$"""
            {
              runtime {
                runtimeEntities(ckId: "{{CarCkTypeId}}", first: 10) {
                  items { associations { definitions({{arguments}}) { items { targetRtId } } } }
                }
              }
            }
            """);

        if (refused)
        {
            result.Errors.Should().NotBeNullOrEmpty().And.OnlyContain(e => e.Code == "ASSET1007");
        }
        else
        {
            result.Errors.Should().BeNullOrEmpty();
        }
    }

    [Fact]
    public async Task Enforce_GenericAssociationsOfATypedEntityAreChecked()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;

        var result = await ExecuteAsync($$"""
            {
              runtime {
                assetRepositoryIntegrationTestCar(first: 10) {
                  items {
                    associations(roleId: "System/ParentChild", direction: INBOUND, ckId: "{{ReadingCkTypeId}}") {
                      items { rtId }
                    }
                  }
                }
              }
            }
            """);

        result.Errors.Should().NotBeNullOrEmpty().And.OnlyContain(e => e.Code == "ASSET1007");
    }

    // --- Every navigation connection of the real schema is guarded ---

    [Fact]
    public async Task Schema_EveryConnectionOfEntitiesInsideAnEntityIsGuarded()
    {
        ISchema? schema = null;
        await _fixture.ExecuteGraphQlAsync("{ runtime { assetRepositoryIntegrationTestCustomer(first: 1) { totalCount } } }",
            configure: options => options.Listeners.Add(new SchemaCapture(s => schema = s)));
        schema.Should().NotBeNull();

        var unguarded = new List<string>();
        var navigations = 0;
        foreach (var type in schema!.AllTypes.OfType<IObjectGraphType>())
        {
            if (type.Name is RuntimeQueryGuardSchemaVisitor.RuntimeTypeName or "RtTransientQuery" or "RtQuery" ||
                type.Name.EndsWith("Connection", StringComparison.Ordinal) ||
                type.Name.EndsWith("Edge", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var field in type.Fields)
            {
                if (!ReturnsEntityConnection(field))
                {
                    continue;
                }

                navigations++;
                if (RuntimeQueryGuardSchemaVisitor.GetEntryPoint(field, type) == null ||
                    field.Resolver?.GetType().Name != "GuardedResolver")
                {
                    unguarded.Add($"{type.Name}.{field.Name}");
                }
            }
        }

        _output.WriteLine($"{navigations} navigation connections checked");
        navigations.Should().BeGreaterThan(10);
        unguarded.Should().BeEmpty("every connection of entities inside an entity is a navigation the guard checks");
    }

    // A connection whose items are runtime entities (typed, union, generic) or association records.
    private static bool ReturnsEntityConnection(FieldType field)
    {
        var connection = Unwrap(field.ResolvedType) as IComplexGraphType;
        if (connection == null || !connection.Name.EndsWith("Connection", StringComparison.Ordinal))
        {
            return false;
        }

        var itemType = Unwrap(connection.GetField("items")?.ResolvedType);
        return itemType != null && (itemType.GetType().Name is "RtEntityDtoType" or "RtEntityUnionType"
            or "RtEntityGenericDtoType" or "RtAssociationDtoType");
    }

    private static IGraphType? Unwrap(IGraphType? type)
    {
        while (type is IProvideResolvedType wrapper)
        {
            type = wrapper.ResolvedType;
        }

        return type;
    }

    private sealed class SchemaCapture(Action<ISchema> capture) : IDocumentExecutionListener
    {
        public Task AfterValidationAsync(IExecutionContext context, IValidationResult validationResult)
        {
            capture(context.Schema);
            return Task.CompletedTask;
        }

        public Task BeforeExecutionAsync(IExecutionContext context) => Task.CompletedTask;

        public Task AfterExecutionAsync(IExecutionContext context) => Task.CompletedTask;
    }

    private static string Children(string arguments) => $$"""
        {
          runtime {
            assetRepositoryIntegrationTestCar(first: 10) {
              items {
                children({{arguments}}, ckTypeIds: ["{{ReadingCkTypeId}}"]) {
                  items { ... on AssetRepositoryIntegrationTestVehicleReading { rtId } }
                }
              }
            }
          }
        }
        """;

    private async Task<ExecutionResult> ExecuteAsync(string query)
    {
        var result = await _fixture.ExecuteGraphQlAsync(query, user: Principal());
        _output.WriteLine(_fixture.SerializeGraphQl(result));
        return result;
    }

    private List<Dictionary<string, object?>> Measurements()
    {
        lock (_measurements)
        {
            return _measurements.Where(m => Equals(m[RuntimeQueryGuardMetrics.ClientIdLabel], ClientId)).ToList();
        }
    }

    private static ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity([new Claim("client_id", ClientId), new Claim("sub", "nested-guard-subject")], "Bearer"));
}
