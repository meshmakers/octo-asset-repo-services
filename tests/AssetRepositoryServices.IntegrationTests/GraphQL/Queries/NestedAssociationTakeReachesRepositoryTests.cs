using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
///     The page size each direct nested association resolver passes to
///     <c>ITenantRepository.GetRtAssociationTargetsAsync</c>: 0 (the count-only form, which returns no target document)
///     for a selection of <c>totalCount</c> alone - with an omitted <c>first</c>, <c>first: 0</c> and <c>first: 5</c> -
///     and <c>first</c> when items are selected. The totals alone cannot show this: a resolver that passed <c>first</c>
///     again would report the same totals while loading every target. The query runs against the real schema and
///     database; only the tenant repository is wrapped, to record the <c>take</c> argument of each call.
/// </summary>
[Collection(PersistentQueryCollection.Name)]
public class NestedAssociationTakeReachesRepositoryTests
{
    private const string ReadingCkTypeId = "AssetRepositoryIntegrationTest/VehicleReading";
    private const string CarCkTypeId = "AssetRepositoryIntegrationTest/Car";

    private readonly GraphQlTestFixture _fixture;

    public NestedAssociationTakeReachesRepositoryTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    public static TheoryData<string, string, string, int?> Cases()
    {
        var data = new TheoryData<string, string, string, int?>();
        foreach (var form in new[] { "TypedNavigation", "GenericOfTypedEntity", "GenericTargets" })
        {
            data.Add(form, "", "totalCount", 0);
            data.Add(form, "first: 0", "totalCount", 0);
            data.Add(form, "first: 5", "totalCount", 0);
            data.Add(form, "first: 5", "totalCount items { __typename }", 5);
            data.Add(form, "first: 5", "items { __typename }", 5);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheTakeThatReachesTheRepository_IsZeroForTotalCountAlone_AndFirstOtherwise(string form,
        string paging, string selection, int? expectedTake)
    {
        var takes = await RecordAssociationTakesAsync(form, paging, selection);

        takes.Should().NotBeEmpty($"{form}({paging}) {{ {selection} }} reads the association through the repository");
        takes.Should().AllSatisfy(take => take.Should().Be(expectedTake, $"{form}({paging}) {{ {selection} }}"));
    }

    private async Task<IReadOnlyList<int?>> RecordAssociationTakesAsync(string form, string paging, string selection)
    {
        var withComma = paging.Length == 0 ? "" : ", " + paging;
        var query = form switch
        {
            "TypedNavigation" => $$"""
                { runtime { assetRepositoryIntegrationTestCar(first: 50) { items { rtId
                    nested: children(ckTypeIds: ["{{ReadingCkTypeId}}"]{{withComma}}) { {{selection}} } } } } }
                """,
            "GenericOfTypedEntity" => $$"""
                { runtime { assetRepositoryIntegrationTestCar(first: 50) { items { rtId
                    nested: associations(roleId: "System/ParentChild", direction: INBOUND, ckId: "{{ReadingCkTypeId}}"{{withComma}}) { {{selection}} } } } } }
                """,
            "GenericTargets" => $$"""
                { runtime { runtimeEntities(ckId: "{{CarCkTypeId}}", first: 50) { items { rtId
                    associations { nested: targets(roleId: "System/ParentChild", direction: INBOUND, ckId: "{{ReadingCkTypeId}}"{{withComma}}) { {{selection}} } } } } } }
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
        };

        var takes = new ConcurrentQueue<int?>();
        var result = await _fixture.ExecuteGraphQlAsync(query, configure: options =>
        {
            var userContext = (GraphQlUserContext)options.UserContext;
            options.UserContext = new GraphQlUserContext(userContext.User,
                RecordingTenantContext.Wrap(userContext.TenantContext, takes));
        });

        var answer = JObject.Parse(_fixture.SerializeGraphQl(result));
        result.Errors.Should().BeNullOrEmpty(answer.ToString());
        return takes.ToList();
    }

    /// <summary>
    ///     Forwards every call to the real tenant context; <c>GetTenantRepository</c> returns the real repository wrapped
    ///     in a <see cref="RecordingTenantRepository" />.
    /// </summary>
    public class RecordingTenantContext : DispatchProxy
    {
        private ITenantContext _target = null!;
        private ConcurrentQueue<int?> _takes = null!;

        internal static ITenantContext Wrap(ITenantContext target, ConcurrentQueue<int?> takes)
        {
            var proxy = Create<ITenantContext, RecordingTenantContext>();
            var recording = (RecordingTenantContext)(object)proxy;
            recording._target = target;
            recording._takes = takes;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var result = Unwrap(() => targetMethod!.Invoke(_target, args));
            return targetMethod!.Name == nameof(ITenantContext.GetTenantRepository) && result is ITenantRepository repository
                ? RecordingTenantRepository.Wrap(repository, _takes)
                : result;
        }
    }

    /// <summary>
    ///     Forwards every call to the real repository and records the <c>take</c> argument of every
    ///     <c>GetRtAssociationTargetsAsync</c> call (the direct association read of the nested resolvers).
    /// </summary>
    public class RecordingTenantRepository : DispatchProxy
    {
        private ITenantRepository _target = null!;
        private ConcurrentQueue<int?> _takes = null!;

        internal static ITenantRepository Wrap(ITenantRepository target, ConcurrentQueue<int?> takes)
        {
            var proxy = Create<ITenantRepository, RecordingTenantRepository>();
            var recording = (RecordingTenantRepository)(object)proxy;
            recording._target = target;
            recording._takes = takes;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(ITenantRepository.GetRtAssociationTargetsAsync))
            {
                var parameters = targetMethod.GetParameters();
                var takeIndex = Array.FindIndex(parameters, p => p.Name == "take");
                takeIndex.Should().BeGreaterThanOrEqualTo(0, "every overload of the association read takes a page size");
                _takes.Enqueue((int?)args![takeIndex]);
            }

            return Unwrap(() => targetMethod.Invoke(_target, args));
        }
    }

    private static object? Unwrap(Func<object?> call)
    {
        try
        {
            return call();
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }
}
