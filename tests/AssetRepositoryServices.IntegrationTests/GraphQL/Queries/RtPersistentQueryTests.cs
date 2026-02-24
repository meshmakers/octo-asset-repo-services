using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
/// Tests for persistent GraphQL queries (runtime queries with persisted query definitions).
/// Tests both simple queries and aggregation queries.
/// </summary>
[Collection("Sequential")]
public class RtPersistentQueryTests : IClassFixture<GraphQlTestFixture>
{
    private readonly GraphQlTestFixture _fixture;

    public RtPersistentQueryTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    #region Simple Persistent Queries

    [Fact]
    public async Task PersistentSimpleQuery_CreateAndQuery_ReturnsAllWithColumns()
    {
        // Arrange - Create the persistent query
        var createMutation = """
            mutation {
              runtime {
                systemSimpleRtQuerys {
                  create(entities: [
                    {
                      name: "TestSimpleQuery"
                      description: "Test simple query for integration tests"
                      queryCkTypeId: "AssetRepositoryIntegrationTest/MeteringPoint"
                      columns: ["rtWellKnownName", "meterReading", "operatingStatus"]
                    }
                  ]) {
                    rtId
                    ckTypeId
                    name
                    queryCkTypeId
                    columns
                  }
                }
              }
            }
            """;

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);

        var queryRtId = createAnswer.SelectToken("data.runtime.systemSimpleRtQuerys.create[0].rtId")?.Value<string>();
        queryRtId.Should().NotBeNullOrEmpty();

        try
        {
            // Act - Query using the persistent query
            var query = $$"""
                query {
                  runtime {
                    runtimeQuery(rtId: "{{queryRtId}}") {
                      items {
                        queryRtId
                        associatedCkTypeId
                        columns {
                          attributePath
                          aggregationType
                        }
                        rows {
                          items {
                            ... on RtSimpleQueryRow {
                              rtId
                              ckTypeId
                              cells {
                                items {
                                  attributePath
                                  value
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """;

            var result = await _fixture.ExecuteGraphQlAsync(query);

            // Assert
            result.Should().NotBeNull();
            result.Errors.Should().BeNullOrEmpty();
            result.Data.Should().NotBeNull();

            var json = _fixture.SerializeGraphQl(result);
            var answer = JObject.Parse(json);

            var items = answer.SelectToken("data.runtime.runtimeQuery.items");
            items.Should().NotBeNull();
            items!.Type.Should().Be(JTokenType.Array);

            var queryItems = (JArray)items;
            queryItems.Should().HaveCount(1);

            var queryItem = queryItems[0];

            // Verify queryRtId
            var returnedQueryRtId = queryItem["queryRtId"]?.Value<string>();
            returnedQueryRtId.Should().Be(queryRtId);

            // Verify associatedCkTypeId
            var ckTypeId = queryItem["associatedCkTypeId"]?.Value<string>();
            ckTypeId.Should().Be("AssetRepositoryIntegrationTest/MeteringPoint");

            // Verify columns
            var columns = queryItem.SelectToken("columns");
            columns.Should().NotBeNull();
            var columnArray = (JArray)columns!;
            columnArray.Should().HaveCount(3);

            // Verify aggregationType is NONE for simple queries
            var firstColumn = columnArray[0];
            firstColumn["aggregationType"]?.Value<string>().Should().Be("NONE");

            // Verify rows
            var rows = queryItem.SelectToken("rows.items");
            rows.Should().NotBeNull();
            var rowArray = (JArray)rows!;
            rowArray.Should().HaveCount(8); // 8 active metering points
        }
        finally
        {
            // Cleanup - Delete the persistent query
            await DeletePersistentQuery(queryRtId!);
        }
    }

    [Fact]
    public async Task PersistentSimpleQuery_WithFieldFilter_ReturnsFilteredResults()
    {
        // Arrange - Create the persistent query with field filter
        var createMutation = """
            mutation {
              runtime {
                systemSimpleRtQuerys {
                  create(entities: [
                    {
                      name: "TestFilteredQuery"
                      description: "Test query with field filter"
                      queryCkTypeId: "AssetRepositoryIntegrationTest/MeteringPoint"
                      columns: ["rtWellKnownName", "meterReading"]
                      fieldFilter: [
                        {
                          attributePath: "operatingStatus"
                          operator: EQUALS
                          comparisonValue: "2"
                        }
                      ]
                    }
                  ]) {
                    rtId
                  }
                }
              }
            }
            """;

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);

        var queryRtId = createAnswer.SelectToken("data.runtime.systemSimpleRtQuerys.create[0].rtId")?.Value<string>();
        queryRtId.Should().NotBeNullOrEmpty();

        try
        {
            // Act
            var query = $$"""
                query {
                  runtime {
                    runtimeQuery(rtId: "{{queryRtId}}") {
                      items {
                        rows {
                          items {
                            ... on RtSimpleQueryRow {
                              rtId
                              cells {
                                items {
                                  attributePath
                                  value
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """;

            var result = await _fixture.ExecuteGraphQlAsync(query);

            // Assert
            result.Should().NotBeNull();
            result.Errors.Should().BeNullOrEmpty();

            var json = _fixture.SerializeGraphQl(result);
            var answer = JObject.Parse(json);

            var rows = answer.SelectToken("data.runtime.runtimeQuery.items[0].rows.items");
            rows.Should().NotBeNull();

            var rowArray = (JArray)rows!;
            rowArray.Should().HaveCount(1); // Only 1 metering point with status Maintenance (2)
        }
        finally
        {
            await DeletePersistentQuery(queryRtId!);
        }
    }

    [Fact]
    public async Task PersistentSimpleQuery_WithSortOrder_ReturnsSortedResults()
    {
        // Arrange - Create the persistent query with sort order
        var createMutation = """
            mutation {
              runtime {
                systemSimpleRtQuerys {
                  create(entities: [
                    {
                      name: "TestSortedQuery"
                      description: "Test query with sort order"
                      queryCkTypeId: "AssetRepositoryIntegrationTest/MeteringPoint"
                      columns: ["rtWellKnownName", "meterReading"]
                      sorting: [
                        {
                          attributePath: "meterReading"
                          sortOrder: DESCENDING
                        }
                      ]
                    }
                  ]) {
                    rtId
                  }
                }
              }
            }
            """;

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);

        var queryRtId = createAnswer.SelectToken("data.runtime.systemSimpleRtQuerys.create[0].rtId")?.Value<string>();
        queryRtId.Should().NotBeNullOrEmpty();

        try
        {
            // Act
            var query = $$"""
                query {
                  runtime {
                    runtimeQuery(rtId: "{{queryRtId}}") {
                      items {
                        rows {
                          items {
                            ... on RtSimpleQueryRow {
                              rtId
                              cells {
                                items {
                                  attributePath
                                  value
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """;

            var result = await _fixture.ExecuteGraphQlAsync(query);

            // Assert
            result.Should().NotBeNull();
            result.Errors.Should().BeNullOrEmpty();

            var json = _fixture.SerializeGraphQl(result);
            var answer = JObject.Parse(json);

            var rows = answer.SelectToken("data.runtime.runtimeQuery.items[0].rows.items");
            rows.Should().NotBeNull();

            var rowArray = (JArray)rows!;
            rowArray.Should().HaveCount(8);

            // Verify first row has highest meterReading (156789 from Tech Solutions)
            var firstRowCells = rowArray[0].SelectToken("cells.items");
            firstRowCells.Should().NotBeNull();

            var meterReadingCell = ((JArray)firstRowCells!).FirstOrDefault(c =>
                c["attributePath"]?.Value<string>() == "meterReading");
            meterReadingCell.Should().NotBeNull();

            var meterReading = meterReadingCell!["value"]?.Value<int>();
            meterReading.Should().Be(156789);
        }
        finally
        {
            await DeletePersistentQuery(queryRtId!);
        }
    }

    [Fact]
    public async Task PersistentSimpleQuery_QueryCustomers_ReturnsCustomerData()
    {
        // Arrange
        var createMutation = """
            mutation {
              runtime {
                systemSimpleRtQuerys {
                  create(entities: [
                    {
                      name: "TestCustomerQuery"
                      description: "Test query for customers"
                      queryCkTypeId: "AssetRepositoryIntegrationTest/Customer"
                      columns: ["rtWellKnownName", "firstName", "lastName", "city"]
                    }
                  ]) {
                    rtId
                  }
                }
              }
            }
            """;

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);

        var queryRtId = createAnswer.SelectToken("data.runtime.systemSimpleRtQuerys.create[0].rtId")?.Value<string>();
        queryRtId.Should().NotBeNullOrEmpty();

        try
        {
            // Act
            var query = $$"""
                query {
                  runtime {
                    runtimeQuery(rtId: "{{queryRtId}}") {
                      items {
                        associatedCkTypeId
                        rows {
                          items {
                            ... on RtSimpleQueryRow {
                              rtId
                              rtWellKnownName
                              cells {
                                items {
                                  attributePath
                                  value
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """;

            var result = await _fixture.ExecuteGraphQlAsync(query);

            // Assert
            result.Should().NotBeNull();
            result.Errors.Should().BeNullOrEmpty();

            var json = _fixture.SerializeGraphQl(result);
            var answer = JObject.Parse(json);

            var rows = answer.SelectToken("data.runtime.runtimeQuery.items[0].rows.items");
            rows.Should().NotBeNull();

            var rowArray = (JArray)rows!;
            rowArray.Should().HaveCount(4); // 4 active customers

            // Verify Max Mustermann is in the results
            var maxRow = rowArray.FirstOrDefault(r =>
                r["rtWellKnownName"]?.Value<string>() == "CustomerMaxMustermann");
            maxRow.Should().NotBeNull();

            var maxCells = maxRow!.SelectToken("cells.items");
            maxCells.Should().NotBeNull();

            var firstNameCell = ((JArray)maxCells!).FirstOrDefault(c =>
                c["attributePath"]?.Value<string>() == "firstName");
            firstNameCell.Should().NotBeNull();
            firstNameCell!["value"]?.Value<string>().Should().Be("Max");
        }
        finally
        {
            await DeletePersistentQuery(queryRtId!);
        }
    }

    [Fact]
    public async Task SimpleQuery_WithPagination_ReturnsCorrectRowCount()
    {
        // Arrange - Create a simple query on MeteringPoint (8 active items)
        var createMutation = """
            mutation {
              runtime {
                systemSimpleRtQuerys {
                  create(entities: [
                    {
                      name: "TestPaginationRowCount"
                      description: "Test pagination row count"
                      queryCkTypeId: "AssetRepositoryIntegrationTest/MeteringPoint"
                      columns: ["rtWellKnownName", "meterReading"]
                    }
                  ]) {
                    rtId
                  }
                }
              }
            }
            """;

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);

        var queryRtId = createAnswer.SelectToken("data.runtime.systemSimpleRtQuerys.create[0].rtId")?.Value<string>();
        queryRtId.Should().NotBeNullOrEmpty();

        try
        {
            // Act - Query with first: 2
            var query = $$"""
                query {
                  runtime {
                    runtimeQuery(rtId: "{{queryRtId}}") {
                      items {
                        rows(first: 2) {
                          items {
                            ... on RtSimpleQueryRow {
                              rtId
                            }
                          }
                          pageInfo {
                            hasNextPage
                            endCursor
                          }
                          totalCount
                        }
                      }
                    }
                  }
                }
                """;

            var result = await _fixture.ExecuteGraphQlAsync(query);

            // Assert
            result.Should().NotBeNull();
            result.Errors.Should().BeNullOrEmpty();

            var json = _fixture.SerializeGraphQl(result);
            var answer = JObject.Parse(json);

            var rows = answer.SelectToken("data.runtime.runtimeQuery.items[0].rows.items");
            rows.Should().NotBeNull();
            ((JArray)rows!).Should().HaveCount(2);

            var totalCount = answer.SelectToken("data.runtime.runtimeQuery.items[0].rows.totalCount")?.Value<int>();
            totalCount.Should().Be(8);
        }
        finally
        {
            await DeletePersistentQuery(queryRtId!);
        }
    }

    [Fact]
    public async Task SimpleQuery_WithPagination_HasNextPage_IsTrue()
    {
        // Arrange - Create a simple query on MeteringPoint (8 active items)
        var createMutation = """
            mutation {
              runtime {
                systemSimpleRtQuerys {
                  create(entities: [
                    {
                      name: "TestPaginationHasNextPage"
                      description: "Test pagination hasNextPage"
                      queryCkTypeId: "AssetRepositoryIntegrationTest/MeteringPoint"
                      columns: ["rtWellKnownName"]
                    }
                  ]) {
                    rtId
                  }
                }
              }
            }
            """;

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);

        var queryRtId = createAnswer.SelectToken("data.runtime.systemSimpleRtQuerys.create[0].rtId")?.Value<string>();
        queryRtId.Should().NotBeNullOrEmpty();

        try
        {
            // Act - Query with first: 2 (less than total 8)
            var query = $$"""
                query {
                  runtime {
                    runtimeQuery(rtId: "{{queryRtId}}") {
                      items {
                        rows(first: 2) {
                          pageInfo {
                            hasNextPage
                            hasPreviousPage
                            endCursor
                          }
                        }
                      }
                    }
                  }
                }
                """;

            var result = await _fixture.ExecuteGraphQlAsync(query);

            // Assert
            result.Should().NotBeNull();
            result.Errors.Should().BeNullOrEmpty();

            var json = _fixture.SerializeGraphQl(result);
            var answer = JObject.Parse(json);

            var hasNextPage = answer.SelectToken("data.runtime.runtimeQuery.items[0].rows.pageInfo.hasNextPage")?.Value<bool>();
            hasNextPage.Should().BeTrue();

            var hasPreviousPage = answer.SelectToken("data.runtime.runtimeQuery.items[0].rows.pageInfo.hasPreviousPage")?.Value<bool>();
            hasPreviousPage.Should().BeFalse();

            var endCursor = answer.SelectToken("data.runtime.runtimeQuery.items[0].rows.pageInfo.endCursor")?.Value<string>();
            endCursor.Should().NotBeNullOrEmpty();
        }
        finally
        {
            await DeletePersistentQuery(queryRtId!);
        }
    }

    [Fact]
    public async Task SimpleQuery_WithPagination_SecondPage()
    {
        // Arrange - Create a simple query on MeteringPoint (8 active items)
        var createMutation = """
            mutation {
              runtime {
                systemSimpleRtQuerys {
                  create(entities: [
                    {
                      name: "TestPaginationSecondPage"
                      description: "Test pagination second page"
                      queryCkTypeId: "AssetRepositoryIntegrationTest/MeteringPoint"
                      columns: ["rtWellKnownName"]
                    }
                  ]) {
                    rtId
                  }
                }
              }
            }
            """;

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);

        var queryRtId = createAnswer.SelectToken("data.runtime.systemSimpleRtQuerys.create[0].rtId")?.Value<string>();
        queryRtId.Should().NotBeNullOrEmpty();

        try
        {
            // Act - First page
            var firstPageQuery = $$"""
                query {
                  runtime {
                    runtimeQuery(rtId: "{{queryRtId}}") {
                      items {
                        rows(first: 3) {
                          items {
                            ... on RtSimpleQueryRow {
                              rtId
                            }
                          }
                          pageInfo {
                            hasNextPage
                            endCursor
                          }
                          totalCount
                        }
                      }
                    }
                  }
                }
                """;

            var firstResult = await _fixture.ExecuteGraphQlAsync(firstPageQuery);
            firstResult.Errors.Should().BeNullOrEmpty();

            var firstJson = _fixture.SerializeGraphQl(firstResult);
            var firstAnswer = JObject.Parse(firstJson);

            var endCursor = firstAnswer.SelectToken("data.runtime.runtimeQuery.items[0].rows.pageInfo.endCursor")?.Value<string>();
            endCursor.Should().NotBeNullOrEmpty();

            var firstPageItems = (JArray)firstAnswer.SelectToken("data.runtime.runtimeQuery.items[0].rows.items")!;
            firstPageItems.Should().HaveCount(3);

            // Act - Second page using endCursor from first page
            var secondPageQuery = $$"""
                query {
                  runtime {
                    runtimeQuery(rtId: "{{queryRtId}}") {
                      items {
                        rows(first: 3, after: "{{endCursor}}") {
                          items {
                            ... on RtSimpleQueryRow {
                              rtId
                            }
                          }
                          pageInfo {
                            hasNextPage
                            endCursor
                          }
                          totalCount
                        }
                      }
                    }
                  }
                }
                """;

            var secondResult = await _fixture.ExecuteGraphQlAsync(secondPageQuery);
            secondResult.Errors.Should().BeNullOrEmpty();

            var secondJson = _fixture.SerializeGraphQl(secondResult);
            var secondAnswer = JObject.Parse(secondJson);

            var secondPageItems = (JArray)secondAnswer.SelectToken("data.runtime.runtimeQuery.items[0].rows.items")!;
            secondPageItems.Should().HaveCount(3);

            var secondTotalCount = secondAnswer.SelectToken("data.runtime.runtimeQuery.items[0].rows.totalCount")?.Value<int>();
            secondTotalCount.Should().Be(8);

            // Verify items are different between pages
            var firstPageRtIds = firstPageItems.Select(i => i["rtId"]?.Value<string>()).ToList();
            var secondPageRtIds = secondPageItems.Select(i => i["rtId"]?.Value<string>()).ToList();
            firstPageRtIds.Should().NotIntersectWith(secondPageRtIds);
        }
        finally
        {
            await DeletePersistentQuery(queryRtId!);
        }
    }

    #endregion

    #region Aggregation Persistent Queries

    [Fact]
    public async Task PersistentAggregationQuery_CountMeteringPoints_ReturnsCount()
    {
        // Arrange - Create the persistent aggregation query
        var createMutation = """
            mutation {
              runtime {
                systemAggregationRtQuerys {
                  create(entities: [
                    {
                      name: "TestCountQuery"
                      description: "Test count aggregation query"
                      queryCkTypeId: "AssetRepositoryIntegrationTest/MeteringPoint"
                      columns: [
                        { attributePath: "meterReading", aggregationType: COUNT }
                      ]
                    }
                  ]) {
                    rtId
                    ckTypeId
                    name
                    queryCkTypeId
                    columns {
                      attributePath
                      aggregationType
                    }
                  }
                }
              }
            }
            """;

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);

        var queryRtId = createAnswer.SelectToken("data.runtime.systemAggregationRtQuerys.create[0].rtId")?.Value<string>();
        queryRtId.Should().NotBeNullOrEmpty();

        try
        {
            // Act
            var query = $$"""
                query {
                  runtime {
                    runtimeQuery(rtId: "{{queryRtId}}") {
                      items {
                        associatedCkTypeId
                        columns {
                          attributePath
                          aggregationType
                        }
                        rows {
                          items {
                            ... on RtAggregationQueryRow {
                              ckTypeId
                              cells {
                                items {
                                  attributePath
                                  value
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """;

            var result = await _fixture.ExecuteGraphQlAsync(query);

            // Assert
            result.Should().NotBeNull();
            result.Errors.Should().BeNullOrEmpty();

            var json = _fixture.SerializeGraphQl(result);
            var answer = JObject.Parse(json);

            // Verify columns have COUNT aggregation type
            var columns = answer.SelectToken("data.runtime.runtimeQuery.items[0].columns");
            columns.Should().NotBeNull();
            var columnArray = (JArray)columns!;
            columnArray.Should().HaveCount(1);
            columnArray[0]["aggregationType"]?.Value<string>().Should().Be("COUNT");

            var rows = answer.SelectToken("data.runtime.runtimeQuery.items[0].rows.items");
            rows.Should().NotBeNull();

            var rowArray = (JArray)rows!;
            rowArray.Should().HaveCount(1);

            var cells = rowArray[0].SelectToken("cells.items");
            cells.Should().NotBeNull();

            var countCell = ((JArray)cells!).FirstOrDefault(c =>
                c["attributePath"]?.Value<string>() == "meterReading");
            countCell.Should().NotBeNull();

            // Count should be 8 (active metering points)
            var countValue = countCell!["value"]?.Value<int>();
            countValue.Should().Be(8);
        }
        finally
        {
            await DeletePersistentQuery(queryRtId!);
        }
    }

    [Fact]
    public async Task PersistentAggregationQuery_SumMeterReadings_ReturnsSum()
    {
        // Arrange
        var createMutation = """
            mutation {
              runtime {
                systemAggregationRtQuerys {
                  create(entities: [
                    {
                      name: "TestSumQuery"
                      description: "Test sum aggregation query"
                      queryCkTypeId: "AssetRepositoryIntegrationTest/MeteringPoint"
                      columns: [
                        { attributePath: "meterReading", aggregationType: SUM }
                      ]
                    }
                  ]) {
                    rtId
                  }
                }
              }
            }
            """;

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);

        var queryRtId = createAnswer.SelectToken("data.runtime.systemAggregationRtQuerys.create[0].rtId")?.Value<string>();
        queryRtId.Should().NotBeNullOrEmpty();

        try
        {
            // Act
            var query = $$"""
                query {
                  runtime {
                    runtimeQuery(rtId: "{{queryRtId}}") {
                      items {
                        rows {
                          items {
                            ... on RtAggregationQueryRow {
                              cells {
                                items {
                                  attributePath
                                  value
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """;

            var result = await _fixture.ExecuteGraphQlAsync(query);

            // Assert
            result.Should().NotBeNull();
            result.Errors.Should().BeNullOrEmpty();

            var json = _fixture.SerializeGraphQl(result);
            var answer = JObject.Parse(json);

            var cells = answer.SelectToken("data.runtime.runtimeQuery.items[0].rows.items[0].cells.items");
            cells.Should().NotBeNull();

            var sumCell = ((JArray)cells!).FirstOrDefault(c =>
                c["attributePath"]?.Value<string>() == "meterReading");
            sumCell.Should().NotBeNull();

            // Verify a sum value is returned (should be positive and greater than any single reading)
            var sumValue = sumCell!["value"]?.Value<long>();
            sumValue.Should().BeGreaterThan(0);
        }
        finally
        {
            await DeletePersistentQuery(queryRtId!);
        }
    }

    [Fact]
    public async Task PersistentAggregationQuery_AverageMeterReadings_ReturnsAverage()
    {
        // Arrange
        var createMutation = """
            mutation {
              runtime {
                systemAggregationRtQuerys {
                  create(entities: [
                    {
                      name: "TestAvgQuery"
                      description: "Test average aggregation query"
                      queryCkTypeId: "AssetRepositoryIntegrationTest/MeteringPoint"
                      columns: [
                        { attributePath: "meterReading", aggregationType: AVERAGE }
                      ]
                    }
                  ]) {
                    rtId
                  }
                }
              }
            }
            """;

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);

        var queryRtId = createAnswer.SelectToken("data.runtime.systemAggregationRtQuerys.create[0].rtId")?.Value<string>();
        queryRtId.Should().NotBeNullOrEmpty();

        try
        {
            // Act
            var query = $$"""
                query {
                  runtime {
                    runtimeQuery(rtId: "{{queryRtId}}") {
                      items {
                        rows {
                          items {
                            ... on RtAggregationQueryRow {
                              cells {
                                items {
                                  attributePath
                                  value
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """;

            var result = await _fixture.ExecuteGraphQlAsync(query);

            // Assert
            result.Should().NotBeNull();
            result.Errors.Should().BeNullOrEmpty();

            var json = _fixture.SerializeGraphQl(result);
            var answer = JObject.Parse(json);

            var cells = answer.SelectToken("data.runtime.runtimeQuery.items[0].rows.items[0].cells.items");
            cells.Should().NotBeNull();

            var avgCell = ((JArray)cells!).FirstOrDefault(c =>
                c["attributePath"]?.Value<string>() == "meterReading");
            avgCell.Should().NotBeNull();

            // Verify an average value is returned (should be positive)
            var avgValue = avgCell!["value"]?.Value<double>();
            avgValue.Should().BeGreaterThan(0);
        }
        finally
        {
            await DeletePersistentQuery(queryRtId!);
        }
    }

    [Fact]
    public async Task PersistentAggregationQuery_MinMaxMeterReadings_ReturnsMinMax()
    {
        // Arrange
        var createMutation = """
            mutation {
              runtime {
                systemAggregationRtQuerys {
                  create(entities: [
                    {
                      name: "TestMinMaxQuery"
                      description: "Test min/max aggregation query"
                      queryCkTypeId: "AssetRepositoryIntegrationTest/MeteringPoint"
                      columns: [
                        { attributePath: "meterReading", aggregationType: MINIMUM },
                        { attributePath: "meterReading", aggregationType: MAXIMUM }
                      ]
                    }
                  ]) {
                    rtId
                  }
                }
              }
            }
            """;

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);

        var queryRtId = createAnswer.SelectToken("data.runtime.systemAggregationRtQuerys.create[0].rtId")?.Value<string>();
        queryRtId.Should().NotBeNullOrEmpty();

        try
        {
            // Act
            var query = $$"""
                query {
                  runtime {
                    runtimeQuery(rtId: "{{queryRtId}}") {
                      items {
                        rows {
                          items {
                            ... on RtAggregationQueryRow {
                              cells {
                                items {
                                  attributePath
                                  value
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """;

            var result = await _fixture.ExecuteGraphQlAsync(query);

            // Assert
            result.Should().NotBeNull();
            result.Errors.Should().BeNullOrEmpty();

            var json = _fixture.SerializeGraphQl(result);
            var answer = JObject.Parse(json);

            var cells = answer.SelectToken("data.runtime.runtimeQuery.items[0].rows.items[0].cells.items");
            cells.Should().NotBeNull();

            var cellArray = (JArray)cells!;
            cellArray.Should().HaveCount(2);

            // Verify minimum and maximum values are returned (min should be less than max)
            var minCell = cellArray.FirstOrDefault();
            minCell.Should().NotBeNull();
            var minValue = minCell!["value"]?.Value<int>() ?? 0;
            minValue.Should().BeGreaterThan(0);

            var maxCell = cellArray.LastOrDefault();
            maxCell.Should().NotBeNull();
            var maxValue = maxCell!["value"]?.Value<int>() ?? 0;
            maxValue.Should().BeGreaterThan(0);

            // Min should be less than or equal to max
            minValue.Should().BeLessThanOrEqualTo(maxValue);
        }
        finally
        {
            await DeletePersistentQuery(queryRtId!);
        }
    }

    [Fact]
    public async Task PersistentAggregationQuery_WithFieldFilter_ReturnsFilteredAggregation()
    {
        // Arrange - Filter to only metering points with maintenance status
        var createMutation = """
            mutation {
              runtime {
                systemAggregationRtQuerys {
                  create(entities: [
                    {
                      name: "TestFilteredAggQuery"
                      description: "Test aggregation query with field filter"
                      queryCkTypeId: "AssetRepositoryIntegrationTest/MeteringPoint"
                      columns: [
                        { attributePath: "meterReading", aggregationType: COUNT }
                      ]
                      fieldFilter: [
                        {
                          attributePath: "operatingStatus"
                          operator: EQUALS
                          comparisonValue: "2"
                        }
                      ]
                    }
                  ]) {
                    rtId
                  }
                }
              }
            }
            """;

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);

        var queryRtId = createAnswer.SelectToken("data.runtime.systemAggregationRtQuerys.create[0].rtId")?.Value<string>();
        queryRtId.Should().NotBeNullOrEmpty();

        try
        {
            // Act
            var query = $$"""
                query {
                  runtime {
                    runtimeQuery(rtId: "{{queryRtId}}") {
                      items {
                        rows {
                          items {
                            ... on RtAggregationQueryRow {
                              cells {
                                items {
                                  attributePath
                                  value
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """;

            var result = await _fixture.ExecuteGraphQlAsync(query);

            // Assert
            result.Should().NotBeNull();
            result.Errors.Should().BeNullOrEmpty();

            var json = _fixture.SerializeGraphQl(result);
            var answer = JObject.Parse(json);

            var cells = answer.SelectToken("data.runtime.runtimeQuery.items[0].rows.items[0].cells.items");
            cells.Should().NotBeNull();

            var countCell = ((JArray)cells!).FirstOrDefault();
            countCell.Should().NotBeNull();

            // Only 1 metering point with Maintenance status (operatingStatus = 2)
            var countValue = countCell!["value"]?.Value<int>();
            countValue.Should().Be(1);
        }
        finally
        {
            await DeletePersistentQuery(queryRtId!);
        }
    }

    [Fact]
    public async Task PersistentAggregationQuery_MultipleAggregations_ReturnsAllAggregations()
    {
        // Arrange
        var createMutation = """
            mutation {
              runtime {
                systemAggregationRtQuerys {
                  create(entities: [
                    {
                      name: "TestMultiAggQuery"
                      description: "Test multiple aggregations query"
                      queryCkTypeId: "AssetRepositoryIntegrationTest/MeteringPoint"
                      columns: [
                        { attributePath: "meterReading", aggregationType: COUNT },
                        { attributePath: "meterReading", aggregationType: SUM },
                        { attributePath: "meterReading", aggregationType: AVERAGE },
                        { attributePath: "meterReading", aggregationType: MINIMUM },
                        { attributePath: "meterReading", aggregationType: MAXIMUM }
                      ]
                    }
                  ]) {
                    rtId
                  }
                }
              }
            }
            """;

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);

        var queryRtId = createAnswer.SelectToken("data.runtime.systemAggregationRtQuerys.create[0].rtId")?.Value<string>();
        queryRtId.Should().NotBeNullOrEmpty();

        try
        {
            // Act
            var query = $$"""
                query {
                  runtime {
                    runtimeQuery(rtId: "{{queryRtId}}") {
                      items {
                        columns {
                          attributePath
                          aggregationType
                        }
                        rows {
                          items {
                            ... on RtAggregationQueryRow {
                              cells {
                                items {
                                  attributePath
                                  value
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """;

            var result = await _fixture.ExecuteGraphQlAsync(query);

            // Assert
            result.Should().NotBeNull();
            result.Errors.Should().BeNullOrEmpty();

            var json = _fixture.SerializeGraphQl(result);
            var answer = JObject.Parse(json);

            // Verify columns
            var columns = answer.SelectToken("data.runtime.runtimeQuery.items[0].columns");
            columns.Should().NotBeNull();
            var columnArray = (JArray)columns!;
            columnArray.Should().HaveCount(5);

            // Verify cells
            var cells = answer.SelectToken("data.runtime.runtimeQuery.items[0].rows.items[0].cells.items");
            cells.Should().NotBeNull();
            var cellArray = (JArray)cells!;
            cellArray.Should().HaveCount(5);
        }
        finally
        {
            await DeletePersistentQuery(queryRtId!);
        }
    }

    #endregion

    #region Grouping Aggregation Persistent Queries

    [Fact]
    public async Task PersistentGroupingAggregationQuery_GroupByOperatingStatus_ReturnsGroupedResults()
    {
        // Arrange - Create the persistent grouping aggregation query
        var createMutation = """
            mutation {
              runtime {
                systemGroupingAggregationRtQuerys {
                  create(entities: [
                    {
                      name: "TestGroupingQuery"
                      description: "Test grouping aggregation query"
                      queryCkTypeId: "AssetRepositoryIntegrationTest/MeteringPoint"
                      groupingColumns: ["operatingStatus"]
                      columns: [
                        { attributePath: "meterReading", aggregationType: COUNT }
                      ]
                    }
                  ]) {
                    rtId
                    ckTypeId
                    name
                    queryCkTypeId
                    groupingColumns
                    columns {
                      attributePath
                      aggregationType
                    }
                  }
                }
              }
            }
            """;

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);

        var queryRtId = createAnswer.SelectToken("data.runtime.systemGroupingAggregationRtQuerys.create[0].rtId")?.Value<string>();
        queryRtId.Should().NotBeNullOrEmpty();

        // Verify groupingColumns were saved
        var groupingColumns = createAnswer.SelectToken("data.runtime.systemGroupingAggregationRtQuerys.create[0].groupingColumns");
        groupingColumns.Should().NotBeNull();
        ((JArray)groupingColumns!).Should().HaveCount(1);

        try
        {
            // Act
            var query = $$"""
                query {
                  runtime {
                    runtimeQuery(rtId: "{{queryRtId}}") {
                      items {
                        associatedCkTypeId
                        columns {
                          attributePath
                          aggregationType
                        }
                        rows {
                          items {
                            ... on RtGroupingAggregationQueryRow {
                              ckTypeId
                              cells {
                                items {
                                  attributePath
                                  value
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """;

            var result = await _fixture.ExecuteGraphQlAsync(query);

            // Assert
            result.Should().NotBeNull();
            result.Errors.Should().BeNullOrEmpty();

            var json = _fixture.SerializeGraphQl(result);
            var answer = JObject.Parse(json);

            // Verify columns include both groupBy column and aggregation column
            var columns = answer.SelectToken("data.runtime.runtimeQuery.items[0].columns");
            columns.Should().NotBeNull();
            var columnArray = (JArray)columns!;
            columnArray.Should().HaveCount(2); // operatingStatus (groupBy) + meterReading (aggregation)

            // Verify we have grouped rows (one per distinct operatingStatus value)
            var rows = answer.SelectToken("data.runtime.runtimeQuery.items[0].rows.items");
            rows.Should().NotBeNull();
            var rowArray = (JArray)rows!;
            rowArray.Count.Should().BeGreaterThan(0);

            // Verify first row has both groupBy key and aggregation value as cells
            var firstRowCells = rowArray[0].SelectToken("cells.items");
            firstRowCells.Should().NotBeNull();
            var cellArray = (JArray)firstRowCells!;
            cellArray.Should().HaveCount(2); // operatingStatus key + meterReading count
        }
        finally
        {
            await DeletePersistentQuery(queryRtId!);
        }
    }

    [Fact]
    public async Task PersistentGroupingAggregationQuery_WithMultipleAggregations_ReturnsAllAggregations()
    {
        // Arrange
        var createMutation = """
            mutation {
              runtime {
                systemGroupingAggregationRtQuerys {
                  create(entities: [
                    {
                      name: "TestMultiGroupingQuery"
                      description: "Test grouping with multiple aggregations"
                      queryCkTypeId: "AssetRepositoryIntegrationTest/MeteringPoint"
                      groupingColumns: ["operatingStatus"]
                      columns: [
                        { attributePath: "meterReading", aggregationType: COUNT },
                        { attributePath: "meterReading", aggregationType: SUM },
                        { attributePath: "meterReading", aggregationType: AVERAGE }
                      ]
                    }
                  ]) {
                    rtId
                  }
                }
              }
            }
            """;

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);

        var queryRtId = createAnswer.SelectToken("data.runtime.systemGroupingAggregationRtQuerys.create[0].rtId")?.Value<string>();
        queryRtId.Should().NotBeNullOrEmpty();

        try
        {
            // Act
            var query = $$"""
                query {
                  runtime {
                    runtimeQuery(rtId: "{{queryRtId}}") {
                      items {
                        columns {
                          attributePath
                          aggregationType
                        }
                        rows {
                          items {
                            ... on RtGroupingAggregationQueryRow {
                              cells {
                                items {
                                  attributePath
                                  value
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """;

            var result = await _fixture.ExecuteGraphQlAsync(query);

            // Assert
            result.Should().NotBeNull();
            result.Errors.Should().BeNullOrEmpty();

            var json = _fixture.SerializeGraphQl(result);
            var answer = JObject.Parse(json);

            // Verify columns: 1 groupBy + 3 aggregations = 4 columns
            var columns = answer.SelectToken("data.runtime.runtimeQuery.items[0].columns");
            columns.Should().NotBeNull();
            var columnArray = (JArray)columns!;
            columnArray.Should().HaveCount(4);

            // Verify first row has 4 cells (1 groupBy key + 3 aggregation values)
            var firstRowCells = answer.SelectToken("data.runtime.runtimeQuery.items[0].rows.items[0].cells.items");
            firstRowCells.Should().NotBeNull();
            var cellArray = (JArray)firstRowCells!;
            cellArray.Should().HaveCount(4);
        }
        finally
        {
            await DeletePersistentQuery(queryRtId!);
        }
    }

    [Fact]
    public async Task PersistentGroupingAggregationQuery_WithFieldFilter_ReturnsFilteredGroupedResults()
    {
        // Arrange
        var createMutation = """
            mutation {
              runtime {
                systemGroupingAggregationRtQuerys {
                  create(entities: [
                    {
                      name: "TestFilteredGroupingQuery"
                      description: "Test grouping with field filter"
                      queryCkTypeId: "AssetRepositoryIntegrationTest/MeteringPoint"
                      groupingColumns: ["operatingStatus"]
                      columns: [
                        { attributePath: "meterReading", aggregationType: COUNT }
                      ]
                      fieldFilter: [
                        {
                          attributePath: "operatingStatus"
                          operator: EQUALS
                          comparisonValue: "1"
                        }
                      ]
                    }
                  ]) {
                    rtId
                  }
                }
              }
            }
            """;

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);

        var queryRtId = createAnswer.SelectToken("data.runtime.systemGroupingAggregationRtQuerys.create[0].rtId")?.Value<string>();
        queryRtId.Should().NotBeNullOrEmpty();

        try
        {
            // Act
            var query = $$"""
                query {
                  runtime {
                    runtimeQuery(rtId: "{{queryRtId}}") {
                      items {
                        rows {
                          items {
                            ... on RtGroupingAggregationQueryRow {
                              cells {
                                items {
                                  attributePath
                                  value
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """;

            var result = await _fixture.ExecuteGraphQlAsync(query);

            // Assert
            result.Should().NotBeNull();
            result.Errors.Should().BeNullOrEmpty();

            var json = _fixture.SerializeGraphQl(result);
            var answer = JObject.Parse(json);

            // Should only have one group (operatingStatus = 1)
            var rows = answer.SelectToken("data.runtime.runtimeQuery.items[0].rows.items");
            rows.Should().NotBeNull();
            var rowArray = (JArray)rows!;
            rowArray.Should().HaveCount(1);
        }
        finally
        {
            await DeletePersistentQuery(queryRtId!);
        }
    }

    [Fact]
    public async Task PersistentGroupingAggregationQuery_GroupByCity_ReturnsGroupedByCity()
    {
        // Arrange
        var createMutation = """
            mutation {
              runtime {
                systemGroupingAggregationRtQuerys {
                  create(entities: [
                    {
                      name: "TestCityGroupingQuery"
                      description: "Test grouping by city"
                      queryCkTypeId: "AssetRepositoryIntegrationTest/Customer"
                      groupingColumns: ["city"]
                      columns: [
                        { attributePath: "firstName", aggregationType: COUNT }
                      ]
                    }
                  ]) {
                    rtId
                  }
                }
              }
            }
            """;

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);

        var queryRtId = createAnswer.SelectToken("data.runtime.systemGroupingAggregationRtQuerys.create[0].rtId")?.Value<string>();
        queryRtId.Should().NotBeNullOrEmpty();

        try
        {
            // Act
            var query = $$"""
                query {
                  runtime {
                    runtimeQuery(rtId: "{{queryRtId}}") {
                      items {
                        columns {
                          attributePath
                        }
                        rows {
                          items {
                            ... on RtGroupingAggregationQueryRow {
                              cells {
                                items {
                                  attributePath
                                  value
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """;

            var result = await _fixture.ExecuteGraphQlAsync(query);

            // Assert
            result.Should().NotBeNull();
            result.Errors.Should().BeNullOrEmpty();

            var json = _fixture.SerializeGraphQl(result);
            var answer = JObject.Parse(json);

            // Verify we have groups by city
            var rows = answer.SelectToken("data.runtime.runtimeQuery.items[0].rows.items");
            rows.Should().NotBeNull();
            var rowArray = (JArray)rows!;
            rowArray.Count.Should().BeGreaterThan(0);

            // Verify each row has city as first cell and count as second
            foreach (var row in rowArray)
            {
                var cells = row.SelectToken("cells.items");
                cells.Should().NotBeNull();
                var cellArray = (JArray)cells!;
                cellArray.Should().HaveCount(2);

                // First cell should be city
                var cityCell = cellArray[0];
                cityCell["attributePath"]?.Value<string>().Should().Be("city");

                // Second cell should be count
                var countCell = cellArray[1];
                countCell["attributePath"]?.Value<string>().Should().Be("firstName");
                countCell["value"]?.Value<int>().Should().BeGreaterThan(0);
            }
        }
        finally
        {
            await DeletePersistentQuery(queryRtId!);
        }
    }

    #endregion

    #region Error Cases

    [Fact]
    public async Task PersistentQuery_InvalidRtId_ReturnsError()
    {
        // Arrange
        var query = """
            query {
              runtime {
                runtimeQuery(rtId: "000000000000000000000000") {
                  items {
                    queryRtId
                  }
                }
              }
            }
            """;

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PersistentSimpleQuery_InvalidColumnPath_ReturnsError()
    {
        // Arrange
        var createMutation = """
            mutation {
              runtime {
                systemSimpleRtQuerys {
                  create(entities: [
                    {
                      name: "TestInvalidColumnQuery"
                      description: "Test query with invalid column"
                      queryCkTypeId: "AssetRepositoryIntegrationTest/MeteringPoint"
                      columns: ["invalidColumnPath"]
                    }
                  ]) {
                    rtId
                  }
                }
              }
            }
            """;

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);

        var queryRtId = createAnswer.SelectToken("data.runtime.systemSimpleRtQuerys.create[0].rtId")?.Value<string>();

        try
        {
            // Act
            var query = $$"""
                query {
                  runtime {
                    runtimeQuery(rtId: "{{queryRtId}}") {
                      items {
                        rows {
                          items {
                            ... on RtSimpleQueryRow {
                              rtId
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """;

            var result = await _fixture.ExecuteGraphQlAsync(query);

            // Assert
            result.Should().NotBeNull();
            result.Errors.Should().NotBeNullOrEmpty();
        }
        finally
        {
            if (queryRtId != null)
            {
                await DeletePersistentQuery(queryRtId);
            }
        }
    }

    #endregion

    #region Helper Methods

    private async Task DeletePersistentQuery(string rtId)
    {
        var deleteMutation = $$"""
            mutation {
              runtime {
                systemSimpleRtQuerys {
                  delete(rtIds: ["{{rtId}}"]) {
                    deletedCount
                  }
                }
              }
            }
            """;

        await _fixture.ExecuteGraphQlAsync(deleteMutation);

        // Also try deleting as aggregation query in case it was that type
        var deleteAggMutation = $$"""
            mutation {
              runtime {
                systemAggregationRtQuerys {
                  delete(rtIds: ["{{rtId}}"]) {
                    deletedCount
                  }
                }
              }
            }
            """;

        await _fixture.ExecuteGraphQlAsync(deleteAggMutation);

        // Also try deleting as grouping aggregation query in case it was that type
        var deleteGroupingAggMutation = $$"""
            mutation {
              runtime {
                systemGroupingAggregationRtQuerys {
                  delete(rtIds: ["{{rtId}}"]) {
                    deletedCount
                  }
                }
              }
            }
            """;

        await _fixture.ExecuteGraphQlAsync(deleteGroupingAggMutation);
    }

    #endregion
}
