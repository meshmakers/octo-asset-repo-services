using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
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
        // AB#8049: refused when saved; a query that arrives another way (import, restore) is refused when it runs.
        await AssertStoreRefusedAsync("""columns: ["name", "passwordHash"]""");
        var queryRtId = await StoreAsImportedAsync(q => q.Columns = new AttributeStringValueList(["name", "passwordHash"]));

        AssertRejected(await ExecutePersistedRowsAsync(queryRtId, ""));
    }

    [Theory]
    [InlineData("operator: EQUALS", RtFieldFilterOperatorEnum.Equals, null)] // stored without comparison value
    [InlineData("operator: LIKE, comparisonValue: \"AQ\"", RtFieldFilterOperatorEnum.Like, "AQ")]
    public async Task PersistedQuery_WithAStoredFilterOnAHiddenAttribute_IsRejected(string filter,
        RtFieldFilterOperatorEnum op, string? comparison)
    {
        // The stored filter operator enum (System/FieldFilterOperator) has no IS_NULL / IS_NOT_NULL; every stored
        // filter is checked regardless of its operator or value.
        fixture.OutputHelper = output;
        await AssertStoreRefusedAsync(
            $$"""columns: ["name"], fieldFilter: [{ attributePath: "passwordHash", {{filter}} }]""");
        var queryRtId = await StoreAsImportedAsync(q => q.FieldFilter = HiddenFilter(op, comparison));

        AssertRejected(await ExecutePersistedRowsAsync(queryRtId, ""));
    }

    [Fact]
    public async Task PersistedQuery_Aggregations_CheckTheStoredFilter()
    {
        // Re-review N5: the aggregations sub-connection merges the stored filter - a yes/no oracle with count.
        fixture.OutputHelper = output;
        await AssertStoreRefusedAsync(
            """columns: ["name"], fieldFilter: [{ attributePath: "passwordHash", operator: EQUALS, comparisonValue: "AQ" }]""");
        var queryRtId = await StoreAsImportedAsync(q => q.FieldFilter = HiddenFilter(RtFieldFilterOperatorEnum.Equals, "AQ"));

        var result = await fixture.ExecuteGraphQlAsync($$"""
            { streamData { streamDataQuery(rtId: "{{queryRtId}}") { items {
                aggregations(aggregations: { countAttributePaths: ["name"] }) { items { countStatistics { attributePath value } } }
            } } } }
            """);

        AssertRejected(result);
    }

    [Fact]
    public async Task PersistedQuery_WithAQueryTimeNullFilterOnAHiddenAttribute_IsRejected()
    {
        fixture.OutputHelper = output;
        var queryRtId = await CreatePersistedQueryAsync("""columns: ["name"]""");

        AssertRejected(await ExecutePersistedRowsAsync(queryRtId,
            """(fieldFilter: [{ attributePath: "passwordHash", operator: IS_NULL }])"""));
    }

    [Fact]
    public async Task PersistedQuery_OnVisibleColumnsAndFilters_StillWorks()
    {
        fixture.OutputHelper = output;
        var queryRtId = await CreatePersistedQueryAsync(
            """columns: ["name"], fieldFilter: [{ attributePath: "label", operator: EQUALS, comparisonValue: "x" }]""");

        var result = await ExecutePersistedRowsAsync(queryRtId,
            """(fieldFilter: [{ attributePath: "name", operator: IS_NOT_NULL }])""");

        result.Errors.Should().BeNullOrEmpty(fixture.SerializeGraphQl(result));
    }

    private async Task<string> CreatePersistedQueryAsync(string fields)
    {
        var archiveRtId = await EnsureArchiveAsync();
        var create = await fixture.ExecuteGraphQlAsync($$"""
            mutation {
              runtime { systemSimpleSdQuerys {
                create(entities: [{ name: "hidden-sd", queryCkTypeId: "{{AccountType}}", archiveRtId: "{{archiveRtId}}",
                                    {{fields}} }]) { rtId }
              } }
            }
            """);
        var createJson = fixture.SerializeGraphQl(create);
        create.Errors.Should().BeNullOrEmpty(createJson);
        return JObject.Parse(createJson).SelectToken("data.runtime.systemSimpleSdQuerys.create[0].rtId")!
            .Value<string>()!;
    }

    /// <summary>AB#8049: saving a query that reaches a Hidden attribute is refused, nothing is stored.</summary>
    private async Task AssertStoreRefusedAsync(string fields)
    {
        var archiveRtId = await EnsureArchiveAsync();
        var create = await fixture.ExecuteGraphQlAsync($$"""
            mutation {
              runtime { systemSimpleSdQuerys {
                create(entities: [{ name: "hidden-sd", queryCkTypeId: "{{AccountType}}", archiveRtId: "{{archiveRtId}}",
                                    {{fields}} }]) { rtId }
              } }
            }
            """);
        AssertRejected(create);
    }

    /// <summary>Stores a query through the repository, as an import or a restore would (no save-time check).</summary>
    private async Task<string> StoreAsImportedAsync(Action<RtSimpleSdQuery> configure)
    {
        var archiveRtId = await EnsureArchiveAsync();
        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        var query = await repository.CreateTransientRtEntityAsync<RtSimpleSdQuery>();
        query.Name = "hidden-sd-imported";
        query.QueryCkTypeId = AccountType;
        query.ArchiveRtId = archiveRtId;
        query.Columns = new AttributeStringValueList(["name"]);
        configure(query);
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        await repository.InsertOneRtEntityAsync(session, query);
        await session.CommitTransactionAsync();
        return query.RtId.ToString();
    }

    private static AttributeRecordValueList<RtFieldFilterRecord> HiddenFilter(RtFieldFilterOperatorEnum op,
        string? comparison) =>
        new() { new RtFieldFilterRecord { AttributePath = "passwordHash", Operator = op, ComparisonValue = comparison } };

    private Task<ExecutionResult> ExecutePersistedRowsAsync(string queryRtId, string rowsArguments)
    {
        return fixture.ExecuteGraphQlAsync($$"""
            { streamData { streamDataQuery(rtId: "{{queryRtId}}") { items { rows{{rowsArguments}} { items { timestamp } } } } } }
            """);
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
