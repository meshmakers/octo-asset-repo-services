using FakeItEasy;
using FluentAssertions;
using global::GraphQL.Builders;
using global::GraphQL.Types;
using GraphQLParser.AST;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
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
///     AB#8896, AC 2 at the GraphQL layer: a paged runtime query counts the matching entities only when the selection
///     needs the total - <c>totalCount</c>, <c>pageInfo</c>, or paging from the end with <c>last</c>/<c>before</c>.
///     Otherwise the item query runs with <c>includeTotalCount: false</c>, so no count query is issued.
/// </summary>
public class TotalCountNeededRoutingTests
{
    private static readonly RtCkId<CkTypeId> CkTypeId = new("Test/Thing-1");

    private readonly ITenantRepository _repository = A.Fake<ITenantRepository>();
    private readonly IOctoSession _session = A.Fake<IOctoSession>();

    /// <summary>A connection context with first 10, after the cursor of offset 19 (so the page starts at 20).</summary>
    private static IResolveConnectionContext<object?> Context(params string[] selectedFields)
    {
        var context = A.Fake<IResolveConnectionContext<object?>>();
        A.CallTo(() => context.SubFields).Returns(selectedFields.ToDictionary(f => f,
            f => (new GraphQLField(new GraphQLName(f)), (FieldType)null!)));
        A.CallTo(() => context.First).Returns(10);
        A.CallTo(() => context.After).Returns(ConnectionUtils.OffsetToCursor(19));
        A.CallTo(() => context.Last).Returns(null);
        A.CallTo(() => context.Before).Returns(null); // an unconfigured fake string is "", not null
        return context;
    }

    [Theory]
    [InlineData("items")]
    [InlineData("edges")]
    [InlineData("items", "aggregation")]
    [InlineData("items", "fieldAggregations", "__typename")]
    public async Task ItemsWithoutTotalCountOrPageInfo_RunTheItemQueryWithoutCount(params string[] fields)
    {
        var options = RtEntityQueryOptions.Create();
        A.CallTo(() => _repository.GetRtEntitiesByTypeAsync(_session, CkTypeId, options, 20, 10, false))
            .Returns(ResultSet(10, 30));

        await RuntimeModelQuery.QueryRtEntitiesByTypeAsync(Context(fields), _repository, _session, CkTypeId,
            options, 20);

        A.CallTo(() => _repository.GetRtEntitiesByTypeAsync(_session, CkTypeId, options, 20, 10, false))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _repository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<RtEntityQueryOptions>._, A<int?>._, A<int?>._, true)).MustNotHaveHappened();
        A.CallTo(() => _repository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<RtEntityQueryOptions>._, A<int?>._, A<int?>._)).MustNotHaveHappened();
        A.CallTo(() => _repository.CountRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<RtEntityQueryOptions>._)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData("items", "totalCount")]
    [InlineData("items", "pageInfo")]
    [InlineData("edges", "pageInfo")]
    public async Task TotalCountOrPageInfo_RunTheItemQueryWithCount(params string[] fields)
    {
        var options = RtEntityQueryOptions.Create();
        A.CallTo(() => _repository.GetRtEntitiesByTypeAsync(_session, CkTypeId, options, 20, 10, true))
            .Returns(ResultSet(10, 100));

        var connection = await RuntimeModelQuery.QueryRtEntitiesByTypeAsync(Context(fields), _repository, _session,
            CkTypeId, options, 20);

        ((OctoConnection<RtEntityDto>)connection).TotalCount.Should().Be(100);
        ((OctoConnection<RtEntityDto>)connection).PageInfo!.HasNextPage.Should().BeTrue();
        A.CallTo(() => _repository.GetRtEntitiesByTypeAsync(_session, CkTypeId, options, 20, 10, true))
            .MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData("last")]
    [InlineData("before")]
    public async Task PagingFromTheEnd_RunsTheItemQueryWithCount(string argument)
    {
        var options = RtEntityQueryOptions.Create();
        var context = Context("items");
        A.CallTo(() => context.After).Returns(null);
        if (argument == "last")
        {
            A.CallTo(() => context.Last).Returns(5);
        }
        else
        {
            A.CallTo(() => context.Before).Returns(ConnectionUtils.OffsetToCursor(5));
        }

        A.CallTo(() => _repository.GetRtEntitiesByTypeAsync(_session, CkTypeId, options, null, 10, true))
            .Returns(ResultSet(0, 0));

        await RuntimeModelQuery.QueryRtEntitiesByTypeAsync(context, _repository, _session, CkTypeId, options, null);

        A.CallTo(() => _repository.GetRtEntitiesByTypeAsync(_session, CkTypeId, options, null, 10, true))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ItemsWithoutCount_TheLowerBoundTotalStillCoversThePage()
    {
        // includeTotalCount false: the repository reports skip + returned entities as TotalCount (a lower bound).
        // The connection must still hold every returned entity with the cursors of its real offsets.
        var options = RtEntityQueryOptions.Create();
        A.CallTo(() => _repository.GetRtEntitiesByTypeAsync(_session, CkTypeId, options, 20, 10, false))
            .Returns(ResultSet(10, 30));

        var connection = (OctoConnection<RtEntityDto>)await RuntimeModelQuery.QueryRtEntitiesByTypeAsync(
            Context("items"), _repository, _session, CkTypeId, options, 20);

        connection.Edges.Should().HaveCount(10);
        connection.Edges!.First().Cursor.Should().Be(ConnectionUtils.OffsetToCursor(20));
        connection.Edges!.Last().Cursor.Should().Be(ConnectionUtils.OffsetToCursor(29));
    }

    private static IResultSet<RtEntity> ResultSet(int items, long totalCount)
    {
        var entities = Enumerable.Range(0, items)
            .Select(_ => new RtEntity { RtId = OctoObjectId.GenerateNewId(), CkTypeId = CkTypeId })
            .ToList();
        var resultSet = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => resultSet.Items).Returns(entities);
        A.CallTo(() => resultSet.TotalCount).Returns(totalCount);
        return resultSet;
    }
}
