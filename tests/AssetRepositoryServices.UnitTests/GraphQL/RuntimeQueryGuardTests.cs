using System.Diagnostics.Metrics;
using System.Security.Claims;
using FakeItEasy;
using FluentAssertions;
using GraphQL;
using GraphQL.Execution;
using GraphQL.Types;
using GraphQLParser.AST;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Observability;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     AB#8898: the unbounded runtime-entity query guard. A query that selects items without <c>first</c>, with
///     <c>first</c> above <c>MaxPageSize</c>, or as a key lookup with more keys than <c>MaxPageSize</c> is unbounded;
///     <c>Off</c> does nothing, <c>Warn</c> logs and counts, <c>Enforce</c> also refuses with ASSET1007.
/// </summary>
public sealed class RuntimeQueryGuardTests : IDisposable
{
    private const int Max = 1000;
    private const string TenantId = "meshtest";
    private const string CkTypeId = "Basic.Energy/EdaMessage";

    private readonly RuntimeQueryGuardOptions _options = new();
    private readonly CapturingLogger _logger = new();
    private readonly ServiceProvider _provider;
    private readonly MeterListener _listener = new();
    private readonly List<(long Value, Dictionary<string, object?> Tags)> _measurements = [];
    private readonly RuntimeQueryGuard _guard;

    public RuntimeQueryGuardTests()
    {
        _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = _provider.GetRequiredService<IMeterFactory>();

        // Only the instruments of this test's factory: other tests (and hosts) run in parallel.
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, meterFactory) &&
                instrument.Name == RuntimeQueryGuardMetrics.UnboundedMetricName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            lock (_measurements)
            {
                _measurements.Add((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
            }
        });
        _listener.Start();

        var monitor = A.Fake<IOptionsMonitor<RuntimeQueryGuardOptions>>();
        A.CallTo(() => monitor.CurrentValue).ReturnsLazily(() => _options);
        _guard = new RuntimeQueryGuard(monitor, new RuntimeQueryGuardMetrics(meterFactory), _logger);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _provider.Dispose();
    }

    // --- Classification (AC 2 and the key-lookup rule of 2026-11-12) ---

    public static TheoryData<bool, bool, int?, int?, string> ClassificationCases() => new()
    {
        // selectsItems, isAggregation, first, keyCount, expected
        { true, false, null, null, nameof(RuntimeQueryBoundReason.NoFirst) },
        { true, false, Max + 1, null, nameof(RuntimeQueryBoundReason.FirstAboveLimit) },
        { true, false, Max, null, nameof(RuntimeQueryBoundReason.PagedWithinLimit) },
        { true, false, 1, null, nameof(RuntimeQueryBoundReason.PagedWithinLimit) },
        { true, false, 0, null, nameof(RuntimeQueryBoundReason.PagedWithinLimit) },
        { false, false, null, null, nameof(RuntimeQueryBoundReason.NoItemsSelected) },
        { false, false, Max + 1, null, nameof(RuntimeQueryBoundReason.NoItemsSelected) },
        { true, true, null, null, nameof(RuntimeQueryBoundReason.Aggregation) },
        { true, false, null, 1, nameof(RuntimeQueryBoundReason.KeyLookupWithinLimit) },
        { true, false, null, Max, nameof(RuntimeQueryBoundReason.KeyLookupWithinLimit) },
        { true, false, null, 0, nameof(RuntimeQueryBoundReason.KeyLookupWithinLimit) },
        { true, false, null, Max + 1, nameof(RuntimeQueryBoundReason.TooManyKeys) },
        { true, false, Max + 1, Max + 1, nameof(RuntimeQueryBoundReason.TooManyKeys) },
        { true, false, Max + 1, 5, nameof(RuntimeQueryBoundReason.KeyLookupWithinLimit) },
        { true, false, 10, Max + 1, nameof(RuntimeQueryBoundReason.PagedWithinLimit) }
    };

    [Theory]
    [MemberData(nameof(ClassificationCases))]
    public void Classify_FollowsTheRules(bool selectsItems, bool isAggregation, int? first, int? keyCount,
        string expectedReason)
    {
        var expected = Enum.Parse<RuntimeQueryBoundReason>(expectedReason);
        var reason = RuntimeQueryGuard.Classify(new RuntimeQueryShape(selectsItems, isAggregation, first, keyCount), Max);

        reason.Should().Be(expected);
        RuntimeQueryGuard.IsUnbounded(reason).Should().Be(expected is RuntimeQueryBoundReason.NoFirst
            or RuntimeQueryBoundReason.FirstAboveLimit or RuntimeQueryBoundReason.TooManyKeys);
    }

    [Fact]
    public void Classify_UsesTheConfiguredLimit()
    {
        RuntimeQueryGuard.Classify(new RuntimeQueryShape(true, false, 50, null), 25)
            .Should().Be(RuntimeQueryBoundReason.FirstAboveLimit);
    }

    [Fact]
    public void Options_DefaultToWarnAnd1000()
    {
        var options = new RuntimeQueryGuardOptions();

        options.Mode.Should().Be(RuntimeQueryGuardMode.Warn);
        options.MaxPageSize.Should().Be(1000);
        new RuntimeQueryGuardOptions { MaxPageSize = 0 }.EffectiveMaxPageSize.Should().Be(1000);
    }

    // --- Shape from the GraphQL context ---

    [Theory]
    [InlineData("items", true)]
    [InlineData("edges", true)]
    [InlineData("nodes", true)]
    [InlineData("totalCount", false)]
    [InlineData("pageInfo", false)]
    public void GetShape_DetectsTheItemSelectionByFieldName(string selectedField, bool expected)
    {
        var context = Context(RuntimeQueryEntryPoint.TypedEntities, first: null, selected: selectedField);

        RuntimeQueryGuard.GetShape(context, RuntimeQueryEntryPoint.TypedEntities).SelectsItems.Should().Be(expected);
    }

    [Fact]
    public void GetShape_CountsTheKeysOfAnEntityLookup()
    {
        var keys = Enumerable.Range(0, 3).Select(_ => OctoObjectId.GenerateNewId()).ToList();
        var context = Context(RuntimeQueryEntryPoint.TypedEntities, first: null, rtIds: keys,
            rtId: OctoObjectId.GenerateNewId());

        RuntimeQueryGuard.GetShape(context, RuntimeQueryEntryPoint.TypedEntities).KeyCount.Should().Be(4);
    }

    [Fact]
    public void GetShape_AggregatedColumnsOfAPersistedQueryAreAnAggregation()
    {
        var context = Context(RuntimeQueryEntryPoint.PersistedQueryRows, first: null);
        context.Source = new RtQueryDto
        {
            AssociatedCkTypeId = new RtCkId<CkTypeId>(CkTypeId),
            Columns = [Column(AggregationTypesDto.None), Column(AggregationTypesDto.Sum)]
        };

        RuntimeQueryGuard.GetShape(context, RuntimeQueryEntryPoint.PersistedQueryRows).IsAggregation.Should().BeTrue();
    }

    [Fact]
    public void GetShape_SimpleTransientQueryIsNoAggregation()
    {
        var context = Context(RuntimeQueryEntryPoint.TransientQueryRows, first: null);
        context.Source = new RtTransientQueryDto
        {
            AssociatedCkTypeId = new RtCkId<CkTypeId>(CkTypeId),
            Columns = [Column(AggregationTypesDto.None)]
        };

        RuntimeQueryGuard.GetShape(context, RuntimeQueryEntryPoint.TransientQueryRows).IsAggregation.Should().BeFalse();
    }

    // --- Modes ---

    [Fact]
    public void Off_DoesNothing()
    {
        _options.Mode = RuntimeQueryGuardMode.Off;

        var act = () => _guard.Check(Context(RuntimeQueryEntryPoint.TypedEntities, first: null),
            RuntimeQueryEntryPoint.TypedEntities);

        act.Should().NotThrow();
        _logger.Entries.Should().BeEmpty();
        _measurements.Should().BeEmpty();
    }

    [Fact]
    public void Warn_LogsAndCountsButRuns()
    {
        _options.Mode = RuntimeQueryGuardMode.Warn;

        var act = () => _guard.Check(Context(RuntimeQueryEntryPoint.TypedEntities, first: null, clientId: "studio"),
            RuntimeQueryEntryPoint.TypedEntities);

        act.Should().NotThrow();
        _logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
        var message = _logger.Entries[0].Message;
        message.Should().Contain(CkTypeId).And.Contain(TenantId).And.Contain("studio").And.Contain("ListMessages")
            .And.Contain("NoFirst");
        _measurements.Should().ContainSingle();
        _measurements[0].Value.Should().Be(1);
        _measurements[0].Tags.Should().Contain(RuntimeQueryGuardMetrics.TenantLabel, TenantId)
            .And.Contain(RuntimeQueryGuardMetrics.CkTypeLabel, CkTypeId)
            .And.Contain(RuntimeQueryGuardMetrics.ClientIdLabel, "studio")
            .And.Contain(RuntimeQueryGuardMetrics.ModeLabel, "warn");
    }

    [Fact]
    public void Warn_BoundedQueryIsNeitherLoggedNorCounted()
    {
        _options.Mode = RuntimeQueryGuardMode.Warn;

        _guard.Check(Context(RuntimeQueryEntryPoint.TypedEntities, first: 100), RuntimeQueryEntryPoint.TypedEntities);

        _logger.Entries.Should().BeEmpty();
        _measurements.Should().BeEmpty();
    }

    [Fact]
    public void Enforce_RefusesWithACodedErrorNamingTypeAndLimit()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;
        _options.MaxPageSize = 500;

        var act = () => _guard.Check(Context(RuntimeQueryEntryPoint.TypedEntities, first: 501),
            RuntimeQueryEntryPoint.TypedEntities);

        var error = act.Should().Throw<UnboundedRuntimeQueryException>().Which;
        error.Code.Should().Be("ASSET1007");
        error.Message.Should().Contain(CkTypeId).And.Contain("500").And.Contain(error.ErrorId);
        error.Extensions.Should().ContainKey("errorId").WhoseValue.Should().Be(error.ErrorId);
        var parameters = (IDictionary<string, object?>)error.Extensions!["parameters"]!;
        parameters.Should().Contain("ckTypeId", CkTypeId).And.Contain("maxPageSize", 500)
            .And.Contain("requestedFirst", 501).And.Contain("reason", nameof(RuntimeQueryBoundReason.FirstAboveLimit));
        _logger.Entries.Should().ContainSingle().Which.Message.Should().Contain(error.ErrorId);
        _measurements.Should().ContainSingle().Which.Tags.Should()
            .Contain(RuntimeQueryGuardMetrics.ModeLabel, "enforce")
            .And.Contain(RuntimeQueryGuardMetrics.ClientIdLabel, RuntimeQueryGuardMetrics.UnknownClientId);
    }

    [Fact]
    public void Enforce_KeyLookupWithinTheLimitRuns()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;
        var keys = Enumerable.Range(0, 3).Select(_ => OctoObjectId.GenerateNewId()).ToList();

        var act = () => _guard.Check(Context(RuntimeQueryEntryPoint.TypedEntities, first: null, rtIds: keys),
            RuntimeQueryEntryPoint.TypedEntities);

        act.Should().NotThrow();
        _measurements.Should().BeEmpty();
    }

    [Fact]
    public void Enforce_KeyLookupAboveTheLimitIsRefused()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;
        _options.MaxPageSize = 2;
        var keys = Enumerable.Range(0, 3).Select(_ => OctoObjectId.GenerateNewId()).ToList();

        var act = () => _guard.Check(Context(RuntimeQueryEntryPoint.GenericEntities, first: null, rtIds: keys),
            RuntimeQueryEntryPoint.GenericEntities);

        act.Should().Throw<UnboundedRuntimeQueryException>().Which.CkTypeId.Should().Be(CkTypeId);
    }

    [Fact]
    public void Warn_GenericEntryPointLabelsATypeOutsideTheCkCacheAsUnknown()
    {
        _options.Mode = RuntimeQueryGuardMode.Warn;

        // No ICkCacheService reachable: the caller's ckId must not become a metric series of its own.
        _guard.Check(Context(RuntimeQueryEntryPoint.GenericEntities, first: null), RuntimeQueryEntryPoint.GenericEntities);

        _measurements.Should().ContainSingle().Which.Tags.Should()
            .Contain(RuntimeQueryGuardMetrics.CkTypeLabel, RuntimeQueryGuardMetrics.UnknownCkType);
        _logger.Entries.Should().ContainSingle().Which.Message.Should().Contain(CkTypeId, "the log keeps the raw value");
    }

    [Fact]
    public void IsKnownCkType_OnlyForATypeOfTheCkCache()
    {
        var ckCache = A.Fake<ICkCacheService>();
        A.CallTo(() => ckCache.GetRtCkType(TenantId, A<RtCkId<CkTypeId>>._))
            .Throws(new KeyNotFoundException("unknown type"));

        RuntimeQueryGuard.IsKnownCkType(null, TenantId, CkTypeId).Should().BeFalse();
        RuntimeQueryGuard.IsKnownCkType(ckCache, TenantId, CkTypeId).Should().BeFalse();
        RuntimeQueryGuard.IsKnownCkType(ckCache, TenantId, "not a ck type id").Should().BeFalse();
    }

    [Theory]
    [InlineData(RuntimeQueryGuardMode.Warn)]
    [InlineData(RuntimeQueryGuardMode.Enforce)]
    public void GenericEntryPointWithAMalformedCkTypeIdIsStillClassified(RuntimeQueryGuardMode mode)
    {
        _options.Mode = mode;
        const string malformed = "Model/Type-bad";
        var context = Context(RuntimeQueryEntryPoint.GenericEntities, first: null);
        context.Arguments![Statics.CkIdArg] = new ArgumentValue(malformed, ArgumentSource.Literal);

        var act = () => _guard.Check(context, RuntimeQueryEntryPoint.GenericEntities);

        if (mode == RuntimeQueryGuardMode.Enforce)
        {
            act.Should().Throw<UnboundedRuntimeQueryException>().Which.CkTypeId.Should().Be(malformed);
        }
        else
        {
            act.Should().NotThrow();
        }

        _measurements.Should().ContainSingle().Which.Tags.Should()
            .Contain(RuntimeQueryGuardMetrics.CkTypeLabel, RuntimeQueryGuardMetrics.UnknownCkType);
        _logger.Entries.Should().ContainSingle().Which.Message.Should().Contain(malformed);
    }

    // --- Which fields the schema visitor wraps ---

    [Fact]
    public void SchemaVisitor_WrapsOnlyTheEntryPoints()
    {
        var runtime = new ObjectGraphType { Name = RuntimeQueryGuardSchemaVisitor.RuntimeTypeName };
        var persisted = new ObjectGraphType { Name = RuntimeQueryGuardSchemaVisitor.PersistedQueryTypeName };
        var transient = new ObjectGraphType { Name = RuntimeQueryGuardSchemaVisitor.TransientQueryTypeName };
        var other = new ObjectGraphType { Name = "CkQuery" };

        var typed = new FieldType { Name = "basicEnergyEdaMessage" };
        typed.WithMetadata(Statics.CkId, new RtCkId<CkTypeId>(CkTypeId));

        RuntimeQueryGuardSchemaVisitor.GetEntryPoint(new FieldType { Name = "runtimeEntities" }, runtime)
            .Should().Be(RuntimeQueryEntryPoint.GenericEntities);
        RuntimeQueryGuardSchemaVisitor.GetEntryPoint(typed, runtime).Should().Be(RuntimeQueryEntryPoint.TypedEntities);
        RuntimeQueryGuardSchemaVisitor.GetEntryPoint(new FieldType { Name = "rows" }, persisted)
            .Should().Be(RuntimeQueryEntryPoint.PersistedQueryRows);
        RuntimeQueryGuardSchemaVisitor.GetEntryPoint(new FieldType { Name = "rows" }, transient)
            .Should().Be(RuntimeQueryEntryPoint.TransientQueryRows);

        RuntimeQueryGuardSchemaVisitor.GetEntryPoint(new FieldType { Name = "byInterface" }, runtime).Should().BeNull();
        RuntimeQueryGuardSchemaVisitor.GetEntryPoint(new FieldType { Name = "transientQuery" }, runtime).Should().BeNull();
        RuntimeQueryGuardSchemaVisitor.GetEntryPoint(new FieldType { Name = "aggregations" }, persisted).Should().BeNull();
        RuntimeQueryGuardSchemaVisitor.GetEntryPoint(new FieldType { Name = "rows" }, other).Should().BeNull();
    }

    private static RtQueryColumnDto Column(AggregationTypesDto aggregationType) => new()
    {
        AttributePath = "value",
        AttributeValueType = AttributeValueTypesDto.Int,
        AggregationType = aggregationType
    };

    private static ResolveFieldContext Context(RuntimeQueryEntryPoint entryPoint, int? first, string selected = "items",
        List<OctoObjectId>? rtIds = null, OctoObjectId? rtId = null, string? clientId = null)
    {
        var field = new FieldType { Name = "basicEnergyEdaMessage" };
        if (entryPoint == RuntimeQueryEntryPoint.TypedEntities)
        {
            field.WithMetadata(Statics.CkId, new RtCkId<CkTypeId>(CkTypeId));
        }

        var arguments = new Dictionary<string, ArgumentValue>
        {
            ["first"] = new(first, first == null ? ArgumentSource.FieldDefault : ArgumentSource.Literal)
        };
        if (entryPoint == RuntimeQueryEntryPoint.GenericEntities)
        {
            arguments[Statics.CkIdArg] = new ArgumentValue(CkTypeId, ArgumentSource.Literal);
        }

        if (rtIds != null)
        {
            arguments[Statics.RtIdsArg] = new ArgumentValue(rtIds, ArgumentSource.Literal);
        }

        if (rtId != null)
        {
            arguments[Statics.RtIdArg] = new ArgumentValue(rtId, ArgumentSource.Literal);
        }

        var tenantContext = A.Fake<ITenantContext>();
        A.CallTo(() => tenantContext.TenantId).Returns(TenantId);
        var claims = new List<Claim> { new("sub", "subject-1") };
        if (clientId != null)
        {
            claims.Add(new Claim("client_id", clientId));
        }

        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

        return new ResolveFieldContext
        {
            FieldDefinition = field,
            Arguments = arguments,
            SubFields = new Dictionary<string, (GraphQLField Field, FieldType FieldType)>
            {
                [selected] = (new GraphQLField(new GraphQLName(selected)), new FieldType { Name = selected })
            },
            UserContext = new GraphQlUserContext(user, tenantContext),
            Operation = new GraphQLOperationDefinition(new GraphQLSelectionSet([]))
            {
                Name = new GraphQLName("ListMessages")
            }
        };
    }

    private sealed class CapturingLogger : ILogger<RuntimeQueryGuard>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
