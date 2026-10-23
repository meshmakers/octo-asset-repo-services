using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using GraphQL.Types;
using GraphQL.Validation;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL.RequestHandling;

/// <summary>
///     AB#7020: a mutation request carries either generic mutations or exactly one CK method call; a request that breaks
///     the rule is refused in the validation phase, before any resolver ran. The schema mirrors the real shape: a
///     navigation tree (<c>runtime</c> / <c>accounts</c>) with generic <c>create</c> / <c>remove</c> and method fields
///     that carry the <see cref="Statics.CkMethodCall" /> marker.
/// </summary>
public class OneMethodCallPerRequestRuleTests
{
    private int _resolverRuns;
    private readonly ISchema _schema;

    public OneMethodCallPerRequestRuleTests()
    {
        _schema = BuildSchema();
    }

    [Theory]
    [InlineData("mutation { runtime { accounts { reset(rtId: \"a\") { invocationId } create(name: \"x\") { name } } } }")]
    [InlineData("mutation { runtime { accounts { reset(rtId: \"a\") { invocationId } } remove(id: \"1\") } }")]
    [InlineData("mutation { runtime { accounts { lookup { invocationId } } } runtime2: runtime { remove(id: \"1\") } }")]
    [InlineData("mutation { runtime { accounts { reset(rtId: \"a\") { invocationId } } removeAll } }")]
    public async Task MethodNextToGenericMutation_IsRefusedAsAWhole_NothingRuns(string query)
    {
        var result = await ExecuteAsync(query);

        AssertRefused(result, "MIXED_WITH_GENERIC_MUTATION");
        _resolverRuns.Should().Be(0);
    }

    [Theory]
    [InlineData("mutation { runtime { accounts { reset(rtId: \"a\") { invocationId } lookup { invocationId } } } }")]
    [InlineData("mutation { runtime { accounts { reset(rtId: \"a\") { invocationId } reset(rtId: \"a\") { invocationId } } } }")]
    [InlineData("mutation { runtime { accounts { a: reset(rtId: \"a\") { invocationId } b: reset(rtId: \"a\") { invocationId } } } }")]
    [InlineData("mutation { runtime { accounts { reset(rtId: \"a\") { invocationId } ...F } } } fragment F on AccountMutations { lookup { invocationId } }")]
    [InlineData("mutation { runtime { accounts { reset(rtId: \"a\") { invocationId } ... on AccountMutations { lookup { invocationId } } } } }")]
    [InlineData("mutation { runtime { accounts { reset(rtId: \"a\") { invocationId } } } other: runtime { accounts { lookup { invocationId } } } }")]
    [InlineData("mutation { ...M } fragment M on Mutation { runtime { accounts { reset(rtId: \"a\") { invocationId } lookup { invocationId } } } }")]
    public async Task TwoMethodCalls_AreRefusedAsAWhole_NothingRuns(string query)
    {
        var result = await ExecuteAsync(query);

        AssertRefused(result, "MULTIPLE_METHOD_CALLS");
        _resolverRuns.Should().Be(0);
    }

    [Fact]
    public async Task Refusal_CarriesCodeAndParametersNamingTheMethods()
    {
        var result = await ExecuteAsync(
            "mutation { runtime { accounts { reset(rtId: \"a\") { invocationId } lookup { invocationId } } } }");

        var error = result.Errors!.Single();
        error.Code.Should().Be("ONE_METHOD_CALL_PER_REQUEST");
        error.Message.Should().Be(OneMethodCallPerRequestRule.Message);
        error.Extensions!["code"].Should().Be("ONE_METHOD_CALL_PER_REQUEST");
        var parameters = error.Extensions["parameters"].Should().BeAssignableTo<IDictionary<string, object?>>().Subject;
        parameters["methodIds"].Should().Be("Sample/Account.Lookup-1,Sample/Account.Reset-1");
        parameters["reason"].Should().Be("MULTIPLE_METHOD_CALLS");
    }

    [Fact]
    public async Task Refusal_IsByteIdentical_ForRequestsThatDifferOnlyInArgumentValues()
    {
        var first = await ExecuteAsync(
            "mutation { runtime { accounts { reset(rtId: \"a\") { invocationId } } remove(id: \"1\") } }");
        var second = await ExecuteAsync(
            "mutation { runtime { accounts { reset(rtId: \"another-value\") { invocationId } } remove(id: \"22\") } }");

        Serialize(first).Should().Be(Serialize(second));
    }

    [Theory]
    [InlineData("mutation { runtime { accounts { reset(rtId: \"a\") { invocationId } } } }")]
    [InlineData("mutation { runtime { accounts { lookup { invocationId } } } }")]
    [InlineData("mutation { __typename runtime { accounts { __typename lookup { invocationId } } } }")]
    [InlineData("mutation { ...M } fragment M on Mutation { runtime { accounts { reset(rtId: \"a\") { invocationId } } } }")]
    [InlineData("mutation { runtime { accounts { ...F ...F } } } fragment F on AccountMutations { lookup { invocationId } }")]
    public async Task ExactlyOneMethodCall_PassesAndRuns(string query)
    {
        var result = await ExecuteAsync(query);

        result.Errors.Should().BeNull();
        _resolverRuns.Should().Be(1);
    }

    [Theory]
    [InlineData("mutation { runtime { accounts { create(name: \"x\") { name } } } }", 1)]
    [InlineData("mutation { runtime { accounts { create(name: \"x\") { name } } remove(id: \"1\") removeAll } }", 3)]
    [InlineData("mutation { runtime { accounts { a: create(name: \"x\") { name } b: create(name: \"y\") { name } } } }", 2)]
    public async Task GenericMutationsOnly_AreUntouched(string query, int expectedRuns)
    {
        var result = await ExecuteAsync(query);

        result.Errors.Should().BeNull();
        _resolverRuns.Should().Be(expectedRuns);
    }

    [Fact]
    public async Task OnlyTheSelectedOperationIsJudged()
    {
        const string document = """
            mutation Bad { runtime { accounts { reset(rtId: "a") { invocationId } lookup { invocationId } } } }
            mutation Good { runtime { accounts { lookup { invocationId } } } }
            mutation Generic { runtime { remove(id: "1") } }
            """;

        var good = await ExecuteAsync(document, "Good");
        good.Errors.Should().BeNull();
        _resolverRuns.Should().Be(1);

        _resolverRuns = 0;
        var bad = await ExecuteAsync(document, "Bad");
        AssertRefused(bad, "MULTIPLE_METHOD_CALLS");
        _resolverRuns.Should().Be(0);

        var generic = await ExecuteAsync(document, "Generic");
        generic.Errors.Should().BeNull();
    }

    [Fact]
    public async Task QueriesAndIntrospection_AreNeverJudged()
    {
        var query = await ExecuteAsync("{ accounts { name } __schema { queryType { name } } }");
        query.Errors.Should().BeNull();

        var introspection = await ExecuteAsync("mutation { __typename }");
        introspection.Errors.Should().BeNull();
    }

    [Fact]
    public async Task FragmentCycle_DoesNotLoop()
    {
        var result = await ExecuteAsync(
            "mutation { runtime { accounts { ...A } } } fragment A on AccountMutations { lookup { invocationId } ...B } fragment B on AccountMutations { ...A }");

        // The cycle itself is reported by the core rules; the point is that the rule terminates.
        result.Errors.Should().NotBeNull();
        result.Errors!.Select(e => e.Code).Should().NotContain("ONE_METHOD_CALL_PER_REQUEST");
    }

    [Fact]
    public void Predicate_CountsEveryGeneratedMethodField_AsyncIncluded_AndNothingElse()
    {
        // Pins today's behavior (AB#7021 undecided): there is no async flag on the marker, every method field counts.
        var instance = new FieldType { Name = "a" };
        instance.Metadata[Statics.CkMethodCall] = Info(CkMethodKindDto.Instance);
        var @static = new FieldType { Name = "b" };
        @static.Metadata[Statics.CkMethodCall] = Info(CkMethodKindDto.Static);
        var generic = new FieldType { Name = "create" };
        generic.Metadata[Statics.CkId] = "Sample/Account";

        OneMethodCallPerRequestRule.CountsAsMethodCall(instance).Should().BeTrue();
        OneMethodCallPerRequestRule.CountsAsMethodCall(@static).Should().BeTrue();
        OneMethodCallPerRequestRule.CountsAsMethodCall(generic).Should().BeFalse();
    }

    private static void AssertRefused(ExecutionResult result, string reason)
    {
        result.Errors.Should().NotBeNull();
        var error = result.Errors!.Should().ContainSingle().Subject;
        error.Code.Should().Be("ONE_METHOD_CALL_PER_REQUEST");
        var parameters = (IDictionary<string, object?>)error.Extensions!["parameters"]!;
        parameters["reason"].Should().Be(reason);
    }

    private static string Serialize(ExecutionResult result) => new GraphQLSerializer().Serialize(result);

    private Task<ExecutionResult> ExecuteAsync(string query, string? operationName = null) =>
        new DocumentExecuter().ExecuteAsync(o =>
        {
            o.Schema = _schema;
            o.Query = query;
            o.OperationName = operationName;
            o.ValidationRules = DocumentValidator.CoreRules.Append(new OneMethodCallPerRequestRule()).ToList();
        });

    private static CkMethodCallInfo Info(CkMethodKindDto kind) =>
        new(new RtCkId<CkTypeId>("Sample/Account"), "M-1", "Sample/Account.M-1", kind);

    private Schema BuildSchema()
    {
        var account = new ObjectGraphType { Name = "Account" };
        account.Field<StringGraphType>("name").Resolve(_ => "n");

        var result = new ObjectGraphType { Name = "AccountMethodResult" };
        result.Field<StringGraphType>("invocationId").Resolve(_ => "id");

        var query = new ObjectGraphType { Name = "Query" };
        query.Field("accounts", new ListGraphType(account)).Resolve(_ => new[] { new object() });

        var accountMutations = new ObjectGraphType { Name = "AccountMutations" };
        accountMutations.Field("create", new ListGraphType(account))
            .Argument<StringGraphType>("name")
            .Resolve(_ =>
            {
                _resolverRuns++;
                return new[] { new object() };
            });
        AddMethod(accountMutations, "reset", "Sample/Account.Reset-1", CkMethodKindDto.Instance, result)
            .Argument<StringGraphType>("rtId");
        AddMethod(accountMutations, "lookup", "Sample/Account.Lookup-1", CkMethodKindDto.Static, result);

        var runtime = new ObjectGraphType { Name = "Runtime" };
        runtime.Field("accounts", accountMutations).Resolve(_ => new object());
        runtime.Field<BooleanGraphType>("remove").Argument<StringGraphType>("id").Resolve(_ =>
        {
            _resolverRuns++;
            return true;
        });
        runtime.Field<BooleanGraphType>("removeAll").Resolve(_ =>
        {
            _resolverRuns++;
            return true;
        });

        var mutation = new ObjectGraphType { Name = "Mutation" };
        mutation.Field("runtime", runtime).Resolve(_ => new object());

        return new Schema { Query = query, Mutation = mutation };
    }

    private global::GraphQL.Builders.FieldBuilder<object?, object> AddMethod(ObjectGraphType parent, string name, string qualifiedId,
        CkMethodKindDto kind, ObjectGraphType resultType)
    {
        var builder = parent.Field(name, resultType).Resolve(_ =>
        {
            _resolverRuns++;
            return new object();
        });
        builder.FieldType.Metadata[Statics.CkMethodCall] =
            new CkMethodCallInfo(new RtCkId<CkTypeId>("Sample/Account"), qualifiedId.Split('.')[1], qualifiedId, kind);
        return builder;
    }
}
