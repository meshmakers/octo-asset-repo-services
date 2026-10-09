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
///     CK v2 (review G3 E-M2): stream-data queries refuse every path that reaches a Hidden attribute of the archive's
///     target type (<c>ArchiveHiddenColumnGuard.FindHiddenAttribute</c>) with <c>ATTRIBUTE_NOT_QUERYABLE</c> - columns,
///     group-by, sort, filters (all operators) and aggregations, on the transient, the persisted and the aggregations
///     entry points. The archive targets <c>AccessTestAccount</c> (Hidden <c>PasswordHash</c>) with visible columns only;
///     the engine refuses to activate an archive whose columns reach a Hidden attribute.
/// </summary>
[Collection(StreamDataCollection.Name)]
public class StreamDataHiddenAttributeTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    private const string AccountType = "AssetRepositoryIntegrationTest/AccessTestAccount";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _archiveRtId;

    public static TheoryData<string> HiddenTransientQueries => new()
    {
        """simple(archiveRtId: "{0}", columnPaths: ["name", "passwordHash"]) { items { rows { items { timestamp } } } }""",
        """simple(archiveRtId: "{0}", columnPaths: ["name"], fieldFilter: [{ attributePath: "passwordHash", operator: LIKE, comparisonValue: "AQ" }]) { items { rows { items { timestamp } } } }""",
        """simple(archiveRtId: "{0}", columnPaths: ["name"], fieldFilter: [{ attributePath: "passwordHash", operator: IS_NULL }]) { items { rows { items { timestamp } } } }""",
        """simple(archiveRtId: "{0}", columnPaths: ["name"], sortOrder: [{ attributePath: "passwordHash", sortOrder: ASCENDING }]) { items { rows { items { timestamp } } } }""",
        """simple(archiveRtId: "{0}", columnPaths: ["name"]) { items { rows(sortOrder: [{ attributePath: "passwordHash", sortOrder: ASCENDING }]) { items { timestamp } } } }""",
        """simple(archiveRtId: "{0}", columnPaths: ["name"]) { items { aggregations(aggregations: { maxValueAttributePaths: ["passwordHash"] }) { items { maxStatistics { attributePath } } } } }""",
        """aggregation(archiveRtId: "{0}", columnPaths: [{ attributePath: "passwordHash", aggregationType: COUNT }]) { items { rows { items { cells { items { value } } } } } }""",
        """groupingAggregation(archiveRtId: "{0}", groupByColumnPaths: ["passwordHash"], columnPaths: [{ attributePath: "name", aggregationType: COUNT }]) { items { rows { items { cells { items { value } } } } } }""",
        """downsampling(archiveRtId: "{0}", columnPaths: [{ attributePath: "passwordHash", aggregationType: COUNT }], limit: 4, from: "2026-01-01T00:00:00Z", to: "2026-01-02T00:00:00Z") { items { rows { items { timestamp } } } }"""
    };

    [Theory]
    [MemberData(nameof(HiddenTransientQueries))]
    public async Task TransientQuery_ReachingAHiddenAttribute_IsRejected(string subQuery)
    {
        fixture.OutputHelper = output;
        var archiveRtId = await EnsureArchiveAsync();

        var result = await fixture.ExecuteGraphQlAsync(
            "{ streamData { transientStreamDataQuery { " + subQuery.Replace("{0}", archiveRtId) + " } } }");

        AssertRejected(result);
    }

    [Fact]
    public async Task TransientQuery_OnVisibleColumns_StillWorks()
    {
        fixture.OutputHelper = output;
        var archiveRtId = await EnsureArchiveAsync();

        var result = await fixture.ExecuteGraphQlAsync(
            $$"""{ streamData { transientStreamDataQuery { simple(archiveRtId: "{{archiveRtId}}", columnPaths: ["name", "label"]) { items { rows { totalCount } } } } } }""");

        result.Errors.Should().BeNullOrEmpty(fixture.SerializeGraphQl(result));
    }

    [Fact]
    public async Task PersistedQuery_WithAHiddenColumn_IsRejected()
    {
        fixture.OutputHelper = output;
        var archiveRtId = await EnsureArchiveAsync();
        var create = await fixture.ExecuteGraphQlAsync($$"""
            mutation {
              runtime { systemSimpleSdQuerys {
                create(entities: [{ name: "hidden-sd", queryCkTypeId: "{{AccountType}}", archiveRtId: "{{archiveRtId}}",
                                    columns: ["name", "passwordHash"] }]) { rtId }
              } }
            }
            """);
        var createJson = fixture.SerializeGraphQl(create);
        create.Errors.Should().BeNullOrEmpty(createJson);
        var queryRtId = JObject.Parse(createJson).SelectToken("data.runtime.systemSimpleSdQuerys.create[0].rtId")!
            .Value<string>();

        var result = await fixture.ExecuteGraphQlAsync($$"""
            { streamData { streamDataQuery(rtId: "{{queryRtId}}") { items { rows { items { timestamp } } } } } }
            """);

        AssertRejected(result);
    }

    private void AssertRejected(ExecutionResult result)
    {
        var json = fixture.SerializeGraphQl(result);
        result.Errors.Should().NotBeNull(json);
        result.Errors!.Select(e => e.Code).Should().Contain("ATTRIBUTE_NOT_QUERYABLE", json);
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
                RtWellKnownName = "AccessTestAccountArchive",
                TargetCkTypeId = AccountType,
                Status = RtCkArchiveStatusEnum.Created,
                Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
                {
                    new() { Path = "Name", Indexed = true, Required = false },
                    new() { Path = "Label", Indexed = false, Required = false }
                }
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
