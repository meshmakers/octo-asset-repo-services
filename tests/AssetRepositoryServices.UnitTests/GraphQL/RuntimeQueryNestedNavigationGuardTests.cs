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
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Observability;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     Navigation connections inside an entity follow the rules of the top-level guard (AB#8898). Only to-many
///     navigations are checked; a to-one navigation is exempt in every mode; whatever is not provably to-one counts as
///     to-many. One test per abuse case of the PR description.
/// </summary>
public sealed class RuntimeQueryNestedNavigationGuardTests : IDisposable
{
    private const int Max = 1000;
    private const string TenantId = "meshtest";
    private const string TargetCkTypeId = "Basic.Energy/MeteringPoint";
    private const string ParentChildRole = "System/ParentChild";
    private const string UnknownRole = "NestedGuardProbe/DoesNotExist";

    private static readonly object[] ChildrenPath =
        ["runtime", "basicEnergyCommunity", "items", 0, "children"];

    private readonly RuntimeQueryGuardOptions _options = new();
    private readonly CapturingLogger _logger = new();
    private readonly ServiceProvider _provider;
    private readonly MeterListener _listener = new();
    private readonly List<Dictionary<string, object?>> _measurements = [];
    private readonly RuntimeQueryGuard _guard;
    private readonly ICkCacheService _ckCache = A.Fake<ICkCacheService>();

    public RuntimeQueryNestedNavigationGuardTests()
    {
        _provider = new ServiceCollection().AddMetrics().AddSingleton(_ckCache).BuildServiceProvider();
        var meterFactory = _provider.GetRequiredService<IMeterFactory>();
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

        // ParentChild: outbound Parent is One (to-one), inbound Children is N (to-many).
        A.CallTo(() => _ckCache.GetRtCkAssociationRole(TenantId, A<RtCkId<CkAssociationRoleId>>._))
            .ReturnsLazily((string _, RtCkId<CkAssociationRoleId> roleId) =>
                roleId.ToString().StartsWith(ParentChildRole, StringComparison.Ordinal)
                    ? new CkAssociationRoleGraph(new CkId<CkAssociationRoleId>("System/ParentChild-2"), "Children",
                        "Parent", MultiplicitiesDto.N, MultiplicitiesDto.One, [],
                        new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>(), "parent child")
                    : throw new KeyNotFoundException("unknown role"));

        var monitor = A.Fake<IOptionsMonitor<RuntimeQueryGuardOptions>>();
        A.CallTo(() => monitor.CurrentValue).ReturnsLazily(() => _options);
        _guard = new RuntimeQueryGuard(monitor, new RuntimeQueryGuardMetrics(meterFactory), _logger);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _provider.Dispose();
    }

    // --- AC: to-many typed navigation ---

    [Fact]
    public void Warn_ToManyWithoutFirst_LogsTheSameFieldsAsTopLevelPlusThePathAndRuns()
    {
        _options.Mode = RuntimeQueryGuardMode.Warn;

        var act = () => Check(Typed(toOne: false, clientId: "studio"));

        act.Should().NotThrow();
        var message = _logger.Entries.Should().ContainSingle().Which.Message;
        message.Should().Contain("runtime.basicEnergyCommunity.items.children").And.Contain(TargetCkTypeId)
            .And.Contain(TenantId).And.Contain("studio").And.Contain("subject-1").And.Contain("ListCommunities")
            .And.Contain("NoFirst").And.Contain(nameof(RuntimeQueryEntryPoint.NestedTypedNavigation));
        _measurements.Should().ContainSingle().Which.Should()
            .Contain(RuntimeQueryGuardMetrics.CkTypeLabel, TargetCkTypeId)
            .And.Contain(RuntimeQueryGuardMetrics.TenantLabel, TenantId)
            .And.Contain(RuntimeQueryGuardMetrics.ClientIdLabel, "studio")
            .And.Contain(RuntimeQueryGuardMetrics.ModeLabel, "warn");
    }

    [Fact]
    public void Enforce_ToManyWithoutFirst_IsRefusedWithThePathAndTheLimit()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;

        var act = () => Check(Typed(toOne: false));

        var error = act.Should().Throw<UnboundedRuntimeQueryException>().Which;
        error.Code.Should().Be("ASSET1007");
        error.FieldPath.Should().Be("runtime.basicEnergyCommunity.items.children");
        error.Message.Should().Contain("runtime.basicEnergyCommunity.items.children").And.Contain("1000")
            .And.Contain(TargetCkTypeId).And.Contain(error.ErrorId);
        var parameters = (IDictionary<string, object?>)error.Extensions!["parameters"]!;
        parameters.Should().Contain("fieldPath", "runtime.basicEnergyCommunity.items.children")
            .And.Contain("maxPageSize", Max).And.Contain("reason", nameof(RuntimeQueryBoundReason.NoFirst));
        _logger.Entries.Should().ContainSingle().Which.Message.Should().Contain(error.ErrorId);
        _measurements.Should().ContainSingle().Which.Should().Contain(RuntimeQueryGuardMetrics.ModeLabel, "enforce");
    }

    [Theory]
    [InlineData(1000, false)]
    [InlineData(1001, true)]
    [InlineData(1, false)]
    public void Enforce_ToManyRefusesOnlyAFirstAboveTheLimit(int first, bool refused)
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;

        var act = () => Check(Typed(toOne: false, first: first));

        if (refused)
        {
            act.Should().Throw<UnboundedRuntimeQueryException>().Which.Extensions!["parameters"].Should()
                .BeAssignableTo<IDictionary<string, object?>>().Which.Should()
                .Contain("reason", nameof(RuntimeQueryBoundReason.FirstAboveLimit)).And.Contain("requestedFirst", first);
        }
        else
        {
            act.Should().NotThrow();
            _measurements.Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData("totalCount")]
    [InlineData("pageInfo")]
    public void Enforce_ToManySelectingNoItems_IsNotFlagged(string selected)
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;

        var act = () => Check(Typed(toOne: false, selected: selected));

        act.Should().NotThrow();
        _logger.Entries.Should().BeEmpty();
        _measurements.Should().BeEmpty();
    }

    [Theory]
    [InlineData("edges")]
    [InlineData("nodes")]
    public void Enforce_EdgesAndNodesSelectItemsToo(string selected)
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;

        var act = () => Check(Typed(toOne: false, selected: selected));

        act.Should().Throw<UnboundedRuntimeQueryException>();
    }

    [Fact]
    public void Enforce_AliasOnItems_IsStillAnItemSelection()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;

        // Response key "rows", field name "items": the field name decides.
        var act = () => Check(Typed(toOne: false, selected: "items", responseKey: "rows"));

        act.Should().Throw<UnboundedRuntimeQueryException>();
    }

    [Fact]
    public void Enforce_ToManyKeyLookupWithinTheLimit_IsNotFlagged()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;
        var keys = Enumerable.Range(0, 3).Select(_ => OctoObjectId.GenerateNewId()).ToList();

        var act = () => Check(Typed(toOne: false, rtIds: keys, rtId: OctoObjectId.GenerateNewId()));

        act.Should().NotThrow();
        _measurements.Should().BeEmpty();
    }

    [Fact]
    public void Enforce_ToManyKeyLookupAboveTheLimit_IsRefused()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;
        _options.MaxPageSize = 2;
        var keys = Enumerable.Range(0, 3).Select(_ => OctoObjectId.GenerateNewId()).ToList();

        var act = () => Check(Typed(toOne: false, rtIds: keys));

        act.Should().Throw<UnboundedRuntimeQueryException>().Which.Extensions!["parameters"].Should()
            .BeAssignableTo<IDictionary<string, object?>>().Which.Should()
            .Contain("reason", nameof(RuntimeQueryBoundReason.TooManyKeys)).And.Contain("keyCount", 3);
    }

    // Abuse case: the navigation resolver filters by keys only when it got one, so these load every target.
    public static TheoryData<string> EmptyKeyLookups() => ["rtId: null", "rtIds: null", "rtIds: []", "rtIds: [null]"];

    [Theory]
    [MemberData(nameof(EmptyKeyLookups))]
    public void Enforce_AnEmptyKeyLookupIsNoKeyLookup(string lookup)
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;
        var context = Typed(toOne: false);
        switch (lookup)
        {
            case "rtId: null":
                context.Arguments![Statics.RtIdArg] = new ArgumentValue(null, ArgumentSource.Literal);
                break;
            case "rtIds: null":
                context.Arguments![Statics.RtIdsArg] = new ArgumentValue(null, ArgumentSource.Literal);
                break;
            case "rtIds: []":
                context.Arguments![Statics.RtIdsArg] = new ArgumentValue(new List<object?>(), ArgumentSource.Literal);
                break;
            default:
                context.Arguments![Statics.RtIdsArg] = new ArgumentValue(new List<object?> { null },
                    ArgumentSource.Literal);
                break;
        }

        var act = () => Check(context);

        act.Should().Throw<UnboundedRuntimeQueryException>().Which.Extensions!["parameters"].Should()
            .BeAssignableTo<IDictionary<string, object?>>().Which.Should()
            .Contain("reason", nameof(RuntimeQueryBoundReason.NoFirst)).And.Contain("keyCount", null);
    }

    [Fact]
    public void Enforce_ExplicitFirstNull_IsNoFirst()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;
        var context = Typed(toOne: false);
        context.Arguments!["first"] = new ArgumentValue(null, ArgumentSource.Literal);

        var act = () => Check(context);

        act.Should().Throw<UnboundedRuntimeQueryException>();
    }

    [Fact]
    public void Enforce_FirstFromAVariableAboveTheLimit_IsRefused()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;
        var context = Typed(toOne: false);
        // The executor hands the resolver the variable's value; the source says where it came from.
        context.Arguments!["first"] = new ArgumentValue(5000, ArgumentSource.Variable);

        var act = () => Check(context);

        act.Should().Throw<UnboundedRuntimeQueryException>();
    }

    // --- AC: to-one is exempt in every mode ---

    [Theory]
    [InlineData(RuntimeQueryGuardMode.Warn)]
    [InlineData(RuntimeQueryGuardMode.Enforce)]
    public void ToOneTypedNavigationWithoutFirst_IsNotFlagged(RuntimeQueryGuardMode mode)
    {
        _options.Mode = mode;

        var act = () => Check(Typed(toOne: true));

        act.Should().NotThrow();
        _logger.Entries.Should().BeEmpty();
        _measurements.Should().BeEmpty();
    }

    [Fact]
    public void Enforce_TypedNavigationWithoutMultiplicityMetadata_CountsAsToMany()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;
        var context = Typed(toOne: false);
        context.FieldDefinition.Metadata.Remove(Statics.NavigationToOne);

        var act = () => Check(context);

        act.Should().Throw<UnboundedRuntimeQueryException>();
    }

    [Fact]
    public void IsToOne_OneToManyAssociationBehindTheNameMakesTheFieldToMany()
    {
        var toOne = Association("Parent", MultiplicitiesDto.One);
        var zeroOrOne = Association("Parent", MultiplicitiesDto.ZeroOrOne);
        var toMany = Association("Parent", MultiplicitiesDto.N);

        RtEntityDtoType.IsToOne([toOne, zeroOrOne]).Should().BeTrue();
        RtEntityDtoType.IsToOne([toOne, toMany]).Should().BeFalse("one to-many association makes the field to-many");
        RtEntityDtoType.IsToOne([toMany]).Should().BeFalse();
    }

    [Fact]
    public void Off_NestedNavigationsBehaveAsBefore()
    {
        _options.Mode = RuntimeQueryGuardMode.Off;

        var act = () => Check(Typed(toOne: false));

        act.Should().NotThrow();
        _logger.Entries.Should().BeEmpty();
        _measurements.Should().BeEmpty();
    }

    // --- Once per request, per response path ---

    [Fact]
    public void Warn_ANavigationResolvedForManyParents_IsLoggedAndCountedOnce()
    {
        _options.Mode = RuntimeQueryGuardMode.Warn;
        var userContext = UserContext();

        for (var parent = 0; parent < 5; parent++)
        {
            Check(Typed(toOne: false, userContext: userContext,
                path: ["runtime", "basicEnergyCommunity", "items", parent, "children"]));
        }

        _logger.Entries.Should().ContainSingle();
        _measurements.Should().ContainSingle();
    }

    [Fact]
    public void Enforce_EveryParentGetsTheRefusalWithTheSameErrorId()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;
        var userContext = UserContext();
        var errors = new List<UnboundedRuntimeQueryException>();

        for (var parent = 0; parent < 3; parent++)
        {
            var context = Typed(toOne: false, userContext: userContext,
                path: ["runtime", "basicEnergyCommunity", "items", parent, "children"]);
            errors.Add(((Action)(() => Check(context))).Should().Throw<UnboundedRuntimeQueryException>().Which);
        }

        errors.Select(e => e.ErrorId).Distinct().Should().ContainSingle();
        errors.Should().OnlyHaveUniqueItems("the executor writes path and location into each error it catches");
        _logger.Entries.Should().ContainSingle();
    }

    [Fact]
    public void Warn_TwoAliasesOfOneNavigation_AreTwoDecisions()
    {
        _options.Mode = RuntimeQueryGuardMode.Warn;
        var userContext = UserContext();

        Check(Typed(toOne: false, userContext: userContext, path: ["runtime", "community", "items", 0, "children"],
            responsePath: ["runtime", "community", "items", 0, "kidsA"]));
        Check(Typed(toOne: false, userContext: userContext, path: ["runtime", "community", "items", 0, "children"],
            responsePath: ["runtime", "community", "items", 0, "kidsB"]));

        _logger.Entries.Should().HaveCount(2);
    }

    [Fact]
    public void Enforce_NestedInsideNested_IsCheckedAtEveryLevel()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;
        var userContext = UserContext();

        // Level 1 is paged, level 2 has no first.
        Check(Typed(toOne: false, first: 10, userContext: userContext));
        var deep = Typed(toOne: false, userContext: userContext,
            path: ["runtime", "basicEnergyCommunity", "items", 0, "children", "items", 3, "children"]);
        var act = () => Check(deep);

        act.Should().Throw<UnboundedRuntimeQueryException>().Which.FieldPath.Should()
            .Be("runtime.basicEnergyCommunity.items.children.items.children");
    }

    // --- Generic navigations: multiplicity from role and direction ---

    [Theory]
    [InlineData(nameof(RuntimeQueryEntryPoint.NestedGenericAssociations), GraphDirections.Outbound, false)]
    [InlineData(nameof(RuntimeQueryEntryPoint.NestedGenericAssociations), GraphDirections.Inbound, true)]
    [InlineData(nameof(RuntimeQueryEntryPoint.NestedGenericTargets), GraphDirections.Outbound, false)]
    [InlineData(nameof(RuntimeQueryEntryPoint.NestedGenericTargets), GraphDirections.Inbound, true)]
    [InlineData(nameof(RuntimeQueryEntryPoint.NestedGenericTargets), GraphDirections.Any, true)]
    [InlineData(nameof(RuntimeQueryEntryPoint.NestedGenericDefinitions), GraphDirections.Outbound, false)]
    [InlineData(nameof(RuntimeQueryEntryPoint.NestedGenericDefinitions), GraphDirections.Inbound, true)]
    public void Enforce_GenericNavigation_FollowsTheRoleMultiplicity(string entryPointName,
        GraphDirections direction, bool refused)
    {
        var entryPoint = Enum.Parse<RuntimeQueryEntryPoint>(entryPointName);
        _options.Mode = RuntimeQueryGuardMode.Enforce;

        var act = () => Check(Generic(entryPoint, ParentChildRole, direction), entryPoint);

        if (refused)
        {
            act.Should().Throw<UnboundedRuntimeQueryException>();
        }
        else
        {
            act.Should().NotThrow("the outbound side of ParentChild (Parent) is to-one");
        }
    }

    [Fact]
    public void Enforce_IndirectTraversalOfAToOneRole_CountsAsToMany()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;
        var context = Generic(RuntimeQueryEntryPoint.NestedGenericTargets, ParentChildRole, GraphDirections.Outbound);
        context.Arguments![Statics.IncludeIndirectArg] = new ArgumentValue(true, ArgumentSource.Literal);

        var act = () => Check(context, RuntimeQueryEntryPoint.NestedGenericTargets);

        act.Should().Throw<UnboundedRuntimeQueryException>("every ancestor is returned, not one");
    }

    [Fact]
    public void Enforce_UnknownRole_CountsAsToMany()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;

        var act = () => Check(Generic(RuntimeQueryEntryPoint.NestedGenericTargets, UnknownRole, GraphDirections.Outbound),
            RuntimeQueryEntryPoint.NestedGenericTargets);

        act.Should().Throw<UnboundedRuntimeQueryException>();
    }

    [Fact]
    public void Enforce_DefinitionsWithoutRole_CountAsToMany_AndARelatedRtIdIsAKeyLookup()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;
        var withoutRole = Generic(RuntimeQueryEntryPoint.NestedGenericDefinitions, null, GraphDirections.Outbound);
        var related = Generic(RuntimeQueryEntryPoint.NestedGenericDefinitions, null, GraphDirections.Outbound);
        related.Arguments![Statics.RelatedRtId] = new ArgumentValue(OctoObjectId.GenerateNewId(), ArgumentSource.Literal);

        var refused = () => Check(withoutRole, RuntimeQueryEntryPoint.NestedGenericDefinitions);
        var lookup = () => Check(related, RuntimeQueryEntryPoint.NestedGenericDefinitions);

        refused.Should().Throw<UnboundedRuntimeQueryException>();
        lookup.Should().NotThrow();
    }

    [Fact]
    public void Enforce_GenericNavigationWithoutCkCache_CountsAsToMany()
    {
        _options.Mode = RuntimeQueryGuardMode.Enforce;
        var context = Generic(RuntimeQueryEntryPoint.NestedGenericTargets, ParentChildRole, GraphDirections.Outbound);
        context.RequestServices = null;

        var act = () => Check(context, RuntimeQueryEntryPoint.NestedGenericTargets);

        act.Should().Throw<UnboundedRuntimeQueryException>();
    }

    [Fact]
    public void Warn_GenericNavigationOnATypeOutsideTheCkCache_IsCountedAsUnknownType()
    {
        _options.Mode = RuntimeQueryGuardMode.Warn;
        A.CallTo(() => _ckCache.GetRtCkType(TenantId, A<RtCkId<CkTypeId>>._)).Throws(new KeyNotFoundException("no"));

        Check(Generic(RuntimeQueryEntryPoint.NestedGenericTargets, ParentChildRole, GraphDirections.Inbound),
            RuntimeQueryEntryPoint.NestedGenericTargets);

        _measurements.Should().ContainSingle().Which.Should()
            .Contain(RuntimeQueryGuardMetrics.CkTypeLabel, RuntimeQueryGuardMetrics.UnknownCkType);
    }

    // --- Which fields the schema visitor wraps ---

    [Theory]
    [InlineData(nameof(RuntimeQueryEntryPoint.NestedTypedNavigation))]
    [InlineData(nameof(RuntimeQueryEntryPoint.NestedGenericAssociations))]
    [InlineData(nameof(RuntimeQueryEntryPoint.NestedGenericTargets))]
    [InlineData(nameof(RuntimeQueryEntryPoint.NestedGenericDefinitions))]
    public void SchemaVisitor_WrapsATaggedNavigationOnAnyType(string entryPointName)
    {
        var entryPoint = Enum.Parse<RuntimeQueryEntryPoint>(entryPointName);
        var field = new FieldType { Name = "children" };
        field.WithMetadata(Statics.NestedNavigation, entryPoint);

        RuntimeQueryGuardSchemaVisitor.GetEntryPoint(field, new ObjectGraphType { Name = "SomeMutationResult" })
            .Should().Be(entryPoint);
        RuntimeQueryGuardSchemaVisitor.GetEntryPoint(new FieldType { Name = "children" },
            new ObjectGraphType { Name = "SomeEntity" }).Should().BeNull("an untagged field is no navigation");
    }

    private void Check(ResolveFieldContext context,
        RuntimeQueryEntryPoint entryPoint = RuntimeQueryEntryPoint.NestedTypedNavigation) =>
        _guard.Check(context, entryPoint);

    private static CkTypeAssociationGraph Association(string name, MultiplicitiesDto multiplicity) =>
        new(name, multiplicity, new CkId<CkAssociationRoleId>("System/ParentChild-2"),
            new CkId<CkTypeId>("Basic.Energy/MeteringPoint-1"), new CkId<CkTypeId>("Basic.Energy/Community-1"), null);

    private static GraphQlUserContext UserContext(string? clientId = null)
    {
        var tenantContext = A.Fake<ITenantContext>();
        A.CallTo(() => tenantContext.TenantId).Returns(TenantId);
        var claims = new List<Claim> { new("sub", "subject-1") };
        if (clientId != null)
        {
            claims.Add(new Claim("client_id", clientId));
        }

        return new GraphQlUserContext(new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")), tenantContext);
    }

    private ResolveFieldContext Typed(bool toOne, int? first = null, string selected = "items",
        string? responseKey = null, List<OctoObjectId>? rtIds = null, OctoObjectId? rtId = null,
        string? clientId = null, GraphQlUserContext? userContext = null, object[]? path = null,
        object[]? responsePath = null)
    {
        var field = new FieldType { Name = "children" };
        field.WithMetadata(Statics.NestedNavigation, RuntimeQueryEntryPoint.NestedTypedNavigation);
        field.WithMetadata(Statics.NavigationToOne, toOne);
        field.WithMetadata(Statics.QueryBaseType, new RtCkId<CkTypeId>(TargetCkTypeId));

        var context = Base(field, first, selected, responseKey, clientId, userContext, path, responsePath);
        if (rtIds != null)
        {
            context.Arguments![Statics.RtIdsArg] = new ArgumentValue(rtIds, ArgumentSource.Literal);
        }

        if (rtId != null)
        {
            context.Arguments![Statics.RtIdArg] = new ArgumentValue(rtId, ArgumentSource.Literal);
        }

        return context;
    }

    private ResolveFieldContext Generic(RuntimeQueryEntryPoint entryPoint, string? roleId, GraphDirections direction)
    {
        var field = new FieldType { Name = entryPoint == RuntimeQueryEntryPoint.NestedGenericDefinitions ? "definitions" : "targets" };
        field.WithMetadata(Statics.NestedNavigation, entryPoint);

        var context = Base(field, null, "items", null, null, null, null, null);
        context.Arguments![Statics.DirectionArg] = new ArgumentValue(direction, ArgumentSource.Literal);
        if (roleId != null)
        {
            context.Arguments[Statics.RoleIdArg] = new ArgumentValue(roleId, ArgumentSource.Literal);
        }

        if (entryPoint != RuntimeQueryEntryPoint.NestedGenericDefinitions)
        {
            context.Arguments[Statics.CkIdArg] = new ArgumentValue(TargetCkTypeId, ArgumentSource.Literal);
        }

        return context;
    }

    private ResolveFieldContext Base(FieldType field, int? first, string selected, string? responseKey,
        string? clientId, GraphQlUserContext? userContext, object[]? path, object[]? responsePath)
    {
        var arguments = new Dictionary<string, ArgumentValue>
        {
            ["first"] = new(first, first == null ? ArgumentSource.FieldDefault : ArgumentSource.Literal)
        };

        return new ResolveFieldContext
        {
            FieldDefinition = field,
            Arguments = arguments,
            SubFields = new Dictionary<string, (GraphQLField Field, FieldType FieldType)>
            {
                [responseKey ?? selected] = (new GraphQLField(new GraphQLName(selected)), new FieldType { Name = selected })
            },
            UserContext = userContext ?? UserContext(clientId),
            Path = path ?? ChildrenPath,
            ResponsePath = responsePath ?? path ?? ChildrenPath,
            RequestServices = _provider,
            Operation = new GraphQLOperationDefinition(new GraphQLSelectionSet([]))
            {
                Name = new GraphQLName("ListCommunities")
            }
        };
    }

    private sealed class CapturingLogger : ILogger<RuntimeQueryGuard>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
