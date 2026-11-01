using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
///     AB#8030 — the column metadata of stream-data queries (<c>columns { attributeValueType }</c>) against a real tenant:
///     PascalCase paths (as the EnergyIQ seed stores them, <c>ActivePower</c>) and camelCase paths resolve to the CK value
///     type of the attribute (here <c>Voltage</c> = DOUBLE) instead of STRING; a path that is neither a CK attribute nor a
///     field of the archive is reported as <c>null</c> (unknown); default fields such as <c>timestamp</c> keep STRING.
/// </summary>
[Collection(StreamDataMutatingCollection.Name)]
public class StreamDataColumnTypeTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _archiveRtId;

    [Fact]
    public async Task PersistedSimpleSdQuery_TypesColumnsCaseInsensitively_AndUnknownAsNull()
    {
        fixture.OutputHelper = output;
        var archiveRtId = await EnsureArchiveAsync();
        var queryRtId = await CreatePersistedQueryAsync("systemSimpleSdQuerys", archiveRtId,
            """columns: ["Voltage", "voltage", "MeteringPointNumber", "timestamp", "DoesNotExist"]""");

        var columns = await QueryColumnsAsync(queryRtId);

        columns.Should().Equal(
            ("Voltage", "DOUBLE"),
            ("voltage", "DOUBLE"),
            ("MeteringPointNumber", "STRING"),
            ("timestamp", "STRING"),
            ("DoesNotExist", null));
    }

    [Fact]
    public async Task PersistedAggregationSdQuery_KeepsTheSourceType_ForPascalCasePaths()
    {
        fixture.OutputHelper = output;
        var archiveRtId = await EnsureArchiveAsync();
        var queryRtId = await CreatePersistedQueryAsync("systemAggregationSdQuerys", archiveRtId,
            """
            columns: [
              { attributePath: "Voltage", aggregationType: MAXIMUM },
              { attributePath: "voltage", aggregationType: MINIMUM },
              { attributePath: "MeteringPointNumber", aggregationType: COUNT },
              { attributePath: "DoesNotExist", aggregationType: COUNT }
            ]
            """);

        var columns = await QueryColumnsAsync(queryRtId);

        columns.Select(c => c.Type).Should().Equal("DOUBLE", "DOUBLE", "INTEGER", null);
    }

    [Fact]
    public async Task PersistedGroupingAggregationSdQuery_TypesGroupingAndAggregationColumns()
    {
        fixture.OutputHelper = output;
        var archiveRtId = await EnsureArchiveAsync();
        var queryRtId = await CreatePersistedQueryAsync("systemGroupingAggregationSdQuerys", archiveRtId,
            """
            groupingColumns: ["MeteringPointNumber", "Nope"],
            columns: [{ attributePath: "Voltage", aggregationType: SUM }]
            """);

        var columns = await QueryColumnsAsync(queryRtId);

        columns.Select(c => c.Type).Should().Equal("STRING", null, "DOUBLE");
    }

    [Fact]
    public async Task TransientSimpleQuery_TypesPascalCasePathsLikeTheCkType()
    {
        fixture.OutputHelper = output;
        var archiveRtId = await EnsureArchiveAsync();

        var result = await fixture.ExecuteGraphQlAsync($$"""
            { streamData { transientStreamDataQuery { simple(
                archiveRtId: "{{archiveRtId}}" columnPaths: ["Voltage", "current", "timestamp"] first: 1) {
              items { columns { attributePath attributeValueType } } } } } }
            """);
        var json = AssertSucceeded(result);

        Columns(json.SelectToken("data.streamData.transientStreamDataQuery.simple.items[0].columns")!)
            .Select(c => c.Type).Should().Equal("DOUBLE", "DOUBLE", "STRING");
    }

    private async Task<List<(string Path, string? Type)>> QueryColumnsAsync(string queryRtId)
    {
        var result = await fixture.ExecuteGraphQlAsync($$"""
            { streamData { streamDataQuery(rtId: "{{queryRtId}}") { items {
                columns { attributePath attributeValueType } } } } }
            """);
        var json = AssertSucceeded(result);
        return Columns(json.SelectToken("data.streamData.streamDataQuery.items[0].columns")!);
    }

    private static List<(string Path, string? Type)> Columns(JToken columns)
    {
        return columns
            .Select(c => (c["attributePath"]!.Value<string>()!, c["attributeValueType"]!.Type == JTokenType.Null
                ? null
                : c["attributeValueType"]!.Value<string>()))
            .ToList();
    }

    private JObject AssertSucceeded(ExecutionResult result)
    {
        var json = fixture.SerializeGraphQl(result);
        output.WriteLine(json);
        result.Errors.Should().BeNullOrEmpty(json);
        return JObject.Parse(json);
    }

    private async Task<string> CreatePersistedQueryAsync(string collection, string archiveRtId, string fields)
    {
        var create = await fixture.ExecuteGraphQlAsync($$"""
            mutation {
              runtime { {{collection}} {
                create(entities: [{ name: "coltype-{{Guid.NewGuid():N}}", queryCkTypeId: "{{fixture.TestCkTypeId}}",
                                    archiveRtId: "{{archiveRtId}}", {{fields}} }]) { rtId }
              } }
            }
            """);
        var createJson = fixture.SerializeGraphQl(create);
        create.Errors.Should().BeNullOrEmpty(createJson);
        return JObject.Parse(createJson).SelectToken($"data.runtime.{collection}.create[0].rtId")!.Value<string>()!;
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
                RtWellKnownName = $"ColumnTypeArchive{Guid.NewGuid():N}",
                TargetCkTypeId = fixture.TestCkTypeId,
                Status = RtCkArchiveStatusEnum.Created,
                Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
                {
                    new() { Path = "Voltage", Indexed = true, Required = false },
                    new() { Path = "Current", Indexed = false, Required = false },
                    new() { Path = "MeteringPointNumber", Indexed = false, Required = false },
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
