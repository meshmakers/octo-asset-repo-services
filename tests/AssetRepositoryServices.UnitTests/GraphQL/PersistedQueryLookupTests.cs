using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     AB#8273: the lookup of a persisted query for execution treats a deleted (archived) query like a missing one. The
///     repository's lookup by id returns archived documents too; the GraphQL paths are covered in
///     <c>IntegrationTests/StreamData/DeletedPersistedQueryTests</c>.
/// </summary>
public class PersistedQueryLookupTests
{
    private readonly IRuntimeRepository _repository = A.Fake<IRuntimeRepository>();
    private readonly IOctoSession _session = A.Fake<IOctoSession>();

    [Fact]
    public async Task ActiveQuery_IsReturned()
    {
        var query = new RtSimpleSdQuery { RtId = OctoObjectId.GenerateNewId(), RtState = RtState.Active };
        A.CallTo(() => _repository.GetRtEntityByRtIdAsync<RtStreamDataQuery>(_session, query.RtId))
            .Returns(query);

        var result = await PersistedQueryLookup.GetActiveAsync<RtStreamDataQuery>(_repository, _session, query.RtId);

        result.Should().BeSameAs(query);
    }

    [Fact]
    public async Task ArchivedQuery_IsNotFound()
    {
        var query = new RtSimpleSdQuery { RtId = OctoObjectId.GenerateNewId(), RtState = RtState.Archived };
        A.CallTo(() => _repository.GetRtEntityByRtIdAsync<RtStreamDataQuery>(_session, query.RtId))
            .Returns(query);

        var result = await PersistedQueryLookup.GetActiveAsync<RtStreamDataQuery>(_repository, _session, query.RtId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task MissingQuery_IsNotFound()
    {
        var rtId = OctoObjectId.GenerateNewId();
        A.CallTo(() => _repository.GetRtEntityByRtIdAsync<RtPersistentQuery>(_session, rtId))
            .Returns(Task.FromResult<RtPersistentQuery?>(null));

        var result = await PersistedQueryLookup.GetActiveAsync<RtPersistentQuery>(_repository, _session, rtId);

        result.Should().BeNull();
    }
}
