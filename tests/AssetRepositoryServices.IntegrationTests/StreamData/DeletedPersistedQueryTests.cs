using System.Text.Json;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
///     AB#8273 — a deleted persisted query must not run any more. The default delete archives the entity
///     (<c>rtState = Archived</c>); the lookup by id used to return it anyway, so <c>streamData.streamDataQuery(rtId)</c>
///     (and <c>runtime.runtimeQuery(rtId)</c>) kept answering rows for a query the lists no longer showed (found on test-2
///     <c>energyiq</c>). Now the execution answers the same not-found error as for an id that never existed. An update
///     is visible on the next execution: the definition is read from the database on every call, nothing is cached.
/// </summary>
[Collection(StreamDataMutatingCollection.Name)]
public class DeletedPersistedQueryTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    private const string AggregationSdQueryCkTypeId = "System/AggregationSdQuery";
    private const string SimpleRtQueryCkTypeId = "System/SimpleRtQuery";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _archiveRtId;

    [Fact]
    public async Task DeletedStreamDataQuery_AnswersNotFound_LikeAMissingQuery()
    {
        fixture.OutputHelper = output;
        var archiveRtId = await EnsureArchiveAsync();
        var queryRtId = await CreateAggregationSdQueryAsync(archiveRtId);
        (await QueryColumnPathsAsync(queryRtId)).Should().Equal("voltage_max");

        await DeleteAsync(queryRtId, AggregationSdQueryCkTypeId);

        var deleted = await ExecuteStreamDataQueryAsync(queryRtId);
        var missingRtId = OctoObjectId.GenerateNewId().ToString();
        var missing = await ExecuteStreamDataQueryAsync(missingRtId);

        AssertNotFound(deleted, queryRtId, "data.streamData.streamDataQuery");
        AssertNotFound(missing, missingRtId, "data.streamData.streamDataQuery");
        deleted.Errors!.Single().Code.Should().Be(missing.Errors!.Single().Code);
    }

    [Fact]
    public async Task UpdatedStreamDataQuery_RunsTheNewDefinition()
    {
        fixture.OutputHelper = output;
        var archiveRtId = await EnsureArchiveAsync();
        var queryRtId = await CreateAggregationSdQueryAsync(archiveRtId);
        (await QueryColumnPathsAsync(queryRtId)).Should().Equal("voltage_max");

        var update = await fixture.ExecuteGraphQlAsync($$"""
            mutation {
              runtime { systemAggregationSdQuerys {
                update(entities: [{ rtId: "{{queryRtId}}", item: {
                  columns: [{ attributePath: "Current", aggregationType: MINIMUM }] } }]) { rtId }
              } }
            }
            """);
        update.Errors.Should().BeNullOrEmpty(fixture.SerializeGraphQl(update));

        (await QueryColumnPathsAsync(queryRtId)).Should().Equal("current_min");
    }

    [Fact]
    public async Task DeletedRuntimeQuery_AnswersNotFound()
    {
        fixture.OutputHelper = output;
        var create = await fixture.ExecuteGraphQlAsync($$"""
            mutation {
              runtime { systemSimpleRtQuerys {
                create(entities: [{ name: "deleted-rt-{{Guid.NewGuid():N}}", queryCkTypeId: "{{fixture.TestCkTypeId}}",
                                    columns: ["voltage"] }]) { rtId }
              } }
            }
            """);
        var createJson = fixture.SerializeGraphQl(create);
        create.Errors.Should().BeNullOrEmpty(createJson);
        var queryRtId = JObject.Parse(createJson).SelectToken("data.runtime.systemSimpleRtQuerys.create[0].rtId")!
            .Value<string>()!;

        var query = $$"""{ runtime { runtimeQuery(rtId: "{{queryRtId}}") { totalCount items { queryRtId } } } }""";
        var before = await fixture.ExecuteGraphQlAsync(query);
        before.Errors.Should().BeNullOrEmpty(fixture.SerializeGraphQl(before));

        await DeleteAsync(queryRtId, SimpleRtQueryCkTypeId);

        AssertNotFound(await fixture.ExecuteGraphQlAsync(query), queryRtId, "data.runtime.runtimeQuery");
    }

    private void AssertNotFound(ExecutionResult result, string queryRtId, string dataPath)
    {
        var json = fixture.SerializeGraphQl(result);
        output.WriteLine(json);
        result.Errors.Should().NotBeNull(json);
        result.Errors!.Should().ContainSingle(json).Which.Message.Should()
            .Contain(queryRtId).And.Contain("not found");
        var data = JObject.Parse(json).SelectToken(dataPath);
        (data == null || data.Type == JTokenType.Null).Should().BeTrue("no row may be answered: {0}", json);
    }

    private Task<ExecutionResult> ExecuteStreamDataQueryAsync(string queryRtId) =>
        fixture.ExecuteGraphQlAsync($$"""
            { streamData { streamDataQuery(rtId: "{{queryRtId}}") { totalCount items {
                queryRtId columns { attributePath } } } } }
            """);

    private async Task<List<string>> QueryColumnPathsAsync(string queryRtId)
    {
        var result = await ExecuteStreamDataQueryAsync(queryRtId);
        var json = fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return JObject.Parse(json).SelectToken("data.streamData.streamDataQuery.items[0].columns")!
            .Select(c => c["attributePath"]!.Value<string>()!).ToList();
    }

    private async Task DeleteAsync(string rtId, string ckTypeId)
    {
        var delete = await fixture.ExecuteGraphQlAsync("""
            mutation ($entities: [RtEntityId!]!) {
              runtime { runtimeEntities { delete(entities: $entities) } }
            }
            """, JsonSerializer.Serialize(new { entities = new[] { new { rtId, ckTypeId } } }));
        delete.Errors.Should().BeNullOrEmpty(fixture.SerializeGraphQl(delete));
    }

    private async Task<string> CreateAggregationSdQueryAsync(string archiveRtId)
    {
        var create = await fixture.ExecuteGraphQlAsync($$"""
            mutation {
              runtime { systemAggregationSdQuerys {
                create(entities: [{ name: "deleted-sd-{{Guid.NewGuid():N}}", queryCkTypeId: "{{fixture.TestCkTypeId}}",
                                    archiveRtId: "{{archiveRtId}}",
                                    columns: [{ attributePath: "Voltage", aggregationType: MAXIMUM }] }]) { rtId }
              } }
            }
            """);
        var createJson = fixture.SerializeGraphQl(create);
        create.Errors.Should().BeNullOrEmpty(createJson);
        return JObject.Parse(createJson).SelectToken("data.runtime.systemAggregationSdQuerys.create[0].rtId")!
            .Value<string>()!;
    }

    private async Task<string> EnsureArchiveAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_archiveRtId != null && fixture.Provider != null)
            {
                return _archiveRtId;
            }

            var systemContext = fixture.GetSystemContext();
            var tenantRepository = systemContext.GetSystemTenantRepository();
            var archive = new RtRawArchive
            {
                RtWellKnownName = $"DeletedQueryArchive{Guid.NewGuid():N}",
                TargetCkTypeId = fixture.TestCkTypeId,
                Status = RtCkArchiveStatusEnum.Created,
                Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
                {
                    new() { Path = "Voltage", Indexed = true, Required = false },
                    new() { Path = "Current", Indexed = false, Required = false },
                },
            };
            using (var session = await tenantRepository.GetSessionAsync())
            {
                session.StartTransaction();
                await tenantRepository.InsertOneRtEntityAsync(session, archive);
                await session.CommitTransactionAsync();
            }

            var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
            await tenantContext.GetArchiveLifecycleService()!.ActivateAsync(archive.RtId);

            _archiveRtId = archive.RtId.ToString();
            return _archiveRtId;
        }
        finally
        {
            Gate.Release();
        }
    }
}
