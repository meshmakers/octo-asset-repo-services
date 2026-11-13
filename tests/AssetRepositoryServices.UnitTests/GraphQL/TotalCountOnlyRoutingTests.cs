using FakeItEasy;
using FluentAssertions;
using global::GraphQL.Builders;
using global::GraphQL.Types;
using GraphQLParser.AST;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     AB#8895, AC 1 at the GraphQL layer: a <c>totalCount</c>-only selection of the typed and the generic runtime
///     query and of the rows of a simple persistent query calls the server-side count and never the entity fetch;
///     any other selection keeps the item query.
/// </summary>
public class TotalCountOnlyRoutingTests
{
    private static readonly RtCkId<CkTypeId> CkTypeId = new("Test/Thing-1");

    private readonly ITenantRepository _repository = A.Fake<ITenantRepository>();
    private readonly IOctoSession _session = A.Fake<IOctoSession>();

    private static IResolveConnectionContext<TSource> Context<TSource>(params string[] selectedFields)
    {
        var context = A.Fake<IResolveConnectionContext<TSource>>();
        A.CallTo(() => context.SubFields).Returns(selectedFields.ToDictionary(f => f,
            f => (new GraphQLField(new GraphQLName(f)), (FieldType)null!)));
        return context;
    }

    [Fact]
    public async Task TypeQuery_TotalCountOnly_CountsAndFetchesNoEntity()
    {
        var options = RtEntityQueryOptions.Create();
        A.CallTo(() => _repository.CountRtEntitiesByTypeAsync(_session, CkTypeId, options)).Returns(42L);

        var connection = await RuntimeModelQuery.QueryRtEntitiesByTypeAsync(Context<object?>("totalCount"),
            _repository, _session, CkTypeId, options, null);

        connection.Should().BeOfType<OctoConnection<RtEntityDto>>()
            .Which.TotalCount.Should().Be(42);
        ((OctoConnection<RtEntityDto>)connection).Edges.Should().BeEmpty();
        A.CallTo(() => _repository.CountRtEntitiesByTypeAsync(_session, CkTypeId, options))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _repository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<RtEntityQueryOptions>._, A<int?>._, A<int?>._, A<bool>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task TypeQuery_TotalCountOnlyWithPagingArguments_StillCountsOnly()
    {
        var options = RtEntityQueryOptions.Create();
        A.CallTo(() => _repository.CountRtEntitiesByTypeAsync(_session, CkTypeId, options)).Returns(7L);

        var connection = await RuntimeModelQuery.QueryRtEntitiesByTypeAsync(Context<object?>("totalCount"),
            _repository, _session, CkTypeId, options, 5);

        ((OctoConnection<RtEntityDto>)connection).TotalCount.Should().Be(7, "paging does not change a total count");
        A.CallTo(() => _repository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<RtEntityQueryOptions>._, A<int?>._, A<int?>._, A<bool>._)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData("items")]
    [InlineData("edges")]
    [InlineData("pageInfo")]
    [InlineData("aggregation")]
    public async Task TypeQuery_WithAFieldThatNeedsEntities_RunsTheItemQueryAndNoCount(string field)
    {
        var options = RtEntityQueryOptions.Create();
        A.CallTo(() => _repository.GetRtEntitiesByTypeAsync(_session, CkTypeId, options, null, null, true))
            .Returns(A.Fake<IResultSet<RtEntity>>());

        await RuntimeModelQuery.QueryRtEntitiesByTypeAsync(Context<object?>("totalCount", field), _repository,
            _session, CkTypeId, options, null);

        A.CallTo(() => _repository.GetRtEntitiesByTypeAsync(_session, CkTypeId, options, null, null, true))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _repository.CountRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<RtEntityQueryOptions>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task PersistentQueryRows_TotalCountOnly_CountsAndFetchesNoEntity()
    {
        var options = RtEntityQueryOptions.Create();
        var pairs = new List<NavigationPair>();
        A.CallTo(() => _repository.CountRtEntitiesGraphByTypeAsync(_session, CkTypeId, options, pairs))
            .Returns(3L);

        var connection = await RtQueryDtoType.TryCountRowsAsync(Context<RtQueryDto?>("totalCount"), _repository,
            _session, CkTypeId, options, pairs, isAggregationQuery: false);

        connection.Should().NotBeNull();
        connection!.TotalCount.Should().Be(3);
        connection.Edges.Should().BeEmpty();
        A.CallTo(() => _repository.CountRtEntitiesGraphByTypeAsync(_session, CkTypeId, options, pairs))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _repository.GetRtEntitiesGraphByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<RtEntityQueryOptions>._, A<ICollection<NavigationPair>>._, A<int?>._, A<int?>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task PersistentQueryRows_WithItems_LeaveTheItemQueryToTheResolver()
    {
        var connection = await RtQueryDtoType.TryCountRowsAsync(Context<RtQueryDto?>("totalCount", "items"),
            _repository, _session, CkTypeId, RtEntityQueryOptions.Create(), new List<NavigationPair>(),
            isAggregationQuery: false);

        connection.Should().BeNull();
        A.CallTo(() => _repository.CountRtEntitiesGraphByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<RtEntityQueryOptions>._, A<ICollection<NavigationPair>>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task AggregationQueryRows_TotalCountOnly_AreNotCountedAsEntities()
    {
        var connection = await RtQueryDtoType.TryCountRowsAsync(Context<RtQueryDto?>("totalCount"), _repository,
            _session, CkTypeId, RtEntityQueryOptions.Create(), new List<NavigationPair>(), isAggregationQuery: true);

        connection.Should().BeNull("an aggregation query counts its result rows, not entities");
        A.CallTo(() => _repository.CountRtEntitiesGraphByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<RtEntityQueryOptions>._, A<ICollection<NavigationPair>>._)).MustNotHaveHappened();
    }
}
