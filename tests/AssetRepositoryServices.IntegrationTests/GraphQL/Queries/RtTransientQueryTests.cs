using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using System.Text.Json;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
/// Tests for transient GraphQL queries (runtime queries without persisted query definitions).
/// Tests both simple queries and aggregation queries.
/// </summary>
[Collection("Sequential")]
public class RtTransientQueryTests : IClassFixture<GraphQlTestFixture>
{
    private readonly GraphQlTestFixture _fixture;

    public RtTransientQueryTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    #region Simple Transient Queries

    [Fact]
    public async Task TransientSimpleQuery_QueryMeteringPoints_ReturnsAllWithColumns()
    {
        // Arrange
        var query = """
            query {
              runtime {
                transientQuery {
                  simple(
                    ckId: "AssetRepositoryIntegrationTest/MeteringPoint"
                    columnPaths: ["rtWellKnownName", "meterReading", "operatingStatus"]
                  ) {
                    items {
                      associatedCkTypeId
                      columns {
                        attributePath
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
            }
            """;

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var items = answer.SelectToken("data.runtime.transientQuery.simple.items");
        items.Should().NotBeNull();
        items!.Type.Should().Be(JTokenType.Array);

        var queryItems = (JArray)items;
        queryItems.Should().HaveCount(1);

        var queryItem = queryItems[0];

        // Verify associatedCkTypeId
        var ckTypeId = queryItem["associatedCkTypeId"]?.Value<string>();
        ckTypeId.Should().Be("AssetRepositoryIntegrationTest/MeteringPoint");

        // Verify columns
        var columns = queryItem.SelectToken("columns");
        columns.Should().NotBeNull();
        var columnArray = (JArray)columns!;
        columnArray.Should().HaveCount(3);

        // Verify rows
        var rows = queryItem.SelectToken("rows.items");
        rows.Should().NotBeNull();
        var rowArray = (JArray)rows!;
        rowArray.Should().HaveCount(8); // 8 active metering points
    }

    [Fact]
    public async Task TransientSimpleQuery_WithFieldFilter_ReturnsFilteredResults()
    {
        // Arrange
        var query = """
            query {
              runtime {
                transientQuery {
                  simple(
                    ckId: "AssetRepositoryIntegrationTest/MeteringPoint"
                    columnPaths: ["rtWellKnownName", "meterReading"]
                    fieldFilter: [
                      {
                        attributePath: "operatingStatus"
                        comparisonValue: 2
                        operator: EQUALS
                      }
                    ]
                  ) {
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
            }
            """;

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var rows = answer.SelectToken("data.runtime.transientQuery.simple.items[0].rows.items");
        rows.Should().NotBeNull();

        var rowArray = (JArray)rows!;
        rowArray.Should().HaveCount(1); // Only 1 metering point with status Maintenance (2)
    }

    [Fact]
    public async Task TransientSimpleQuery_WithSortOrder_ReturnsSortedResults()
    {
        // Arrange
        var query = """
            query {
              runtime {
                transientQuery {
                  simple(
                    ckId: "AssetRepositoryIntegrationTest/MeteringPoint"
                    columnPaths: ["rtWellKnownName", "meterReading"]
                    sortOrder: [
                      {
                        attributePath: "meterReading"
                        sortOrder: DESCENDING
                      }
                    ]
                  ) {
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
            }
            """;

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var rows = answer.SelectToken("data.runtime.transientQuery.simple.items[0].rows.items");
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

    [Fact]
    public async Task TransientSimpleQuery_QueryCustomers_ReturnsCustomerData()
    {
        // Arrange
        var query = """
            query {
              runtime {
                transientQuery {
                  simple(
                    ckId: "AssetRepositoryIntegrationTest/Customer"
                    columnPaths: ["rtWellKnownName", "firstName", "lastName", "city"]
                  ) {
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
            }
            """;

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var rows = answer.SelectToken("data.runtime.transientQuery.simple.items[0].rows.items");
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

    #endregion

    #region Aggregation Transient Queries

    [Fact]
    public async Task TransientAggregationQuery_CountMeteringPoints_ReturnsCount()
    {
        // Arrange
        var query = """
            query {
              runtime {
                transientQuery {
                  aggregation(
                    ckId: "AssetRepositoryIntegrationTest/MeteringPoint"
                    columnPaths: [
                      { attributePath: "meterReading", aggregationType: COUNT }
                    ]
                  ) {
                    items {
                      associatedCkTypeId
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
            }
            """;

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var rows = answer.SelectToken("data.runtime.transientQuery.aggregation.items[0].rows.items");
        rows.Should().NotBeNull();

        var rowArray = (JArray)rows!;
        rowArray.Should().HaveCount(1);

        var cells = rowArray[0].SelectToken("cells.items");
        cells.Should().NotBeNull();

        var countCell = ((JArray)cells!).FirstOrDefault(c =>
            c["attributePath"]?.Value<string>() == "meterreading_count");
        countCell.Should().NotBeNull();

        // Count should be 8 (active metering points)
        var countValue = countCell!["value"]?.Value<int>();
        countValue.Should().Be(8);
    }

    [Fact]
    public async Task TransientAggregationQuery_SumMeterReadings_ReturnsSum()
    {
        // Arrange
        var query = """
            query {
              runtime {
                transientQuery {
                  aggregation(
                    ckId: "AssetRepositoryIntegrationTest/MeteringPoint"
                    columnPaths: [
                      { attributePath: "meterReading", aggregationType: SUM }
                    ]
                  ) {
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
            }
            """;

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var cells = answer.SelectToken("data.runtime.transientQuery.aggregation.items[0].rows.items[0].cells.items");
        cells.Should().NotBeNull();

        var sumCell = ((JArray)cells!).FirstOrDefault(c =>
            c["attributePath"]?.Value<string>() == "meterreading_sum");
        sumCell.Should().NotBeNull();

        // Verify a sum value is returned (should be positive and greater than any single reading)
        var sumValue = sumCell!["value"]?.Value<long>();
        sumValue.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task TransientAggregationQuery_AverageMeterReadings_ReturnsAverage()
    {
        // Arrange
        var query = """
            query {
              runtime {
                transientQuery {
                  aggregation(
                    ckId: "AssetRepositoryIntegrationTest/MeteringPoint"
                    columnPaths: [
                      { attributePath: "meterReading", aggregationType: AVERAGE }
                    ]
                  ) {
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
            }
            """;

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var cells = answer.SelectToken("data.runtime.transientQuery.aggregation.items[0].rows.items[0].cells.items");
        cells.Should().NotBeNull();

        var avgCell = ((JArray)cells!).FirstOrDefault(c =>
            c["attributePath"]?.Value<string>() == "meterreading_avg");
        avgCell.Should().NotBeNull();

        // Verify an average value is returned (should be positive)
        var avgValue = avgCell!["value"]?.Value<double>();
        avgValue.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task TransientAggregationQuery_MinMaxMeterReadings_ReturnsMinMax()
    {
        // Arrange
        var query = """
            query {
              runtime {
                transientQuery {
                  aggregation(
                    ckId: "AssetRepositoryIntegrationTest/MeteringPoint"
                    columnPaths: [
                      { attributePath: "meterReading", aggregationType: MINIMUM },
                      { attributePath: "meterReading", aggregationType: MAXIMUM }
                    ]
                  ) {
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
            }
            """;

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var cells = answer.SelectToken("data.runtime.transientQuery.aggregation.items[0].rows.items[0].cells.items");
        cells.Should().NotBeNull();

        var cellArray = (JArray)cells!;
        cellArray.Should().HaveCount(2);

        // Verify minimum and maximum values are returned (min should be less than max).
        // Cells are addressable by their wire-form key (path + function suffix), which is
        // how the engine disambiguates two aggregations on the same source path.
        var minCell = cellArray.FirstOrDefault(c =>
            c["attributePath"]?.Value<string>() == "meterreading_min");
        minCell.Should().NotBeNull();
        var minValue = minCell!["value"]?.Value<int>() ?? 0;
        minValue.Should().BeGreaterThan(0);

        var maxCell = cellArray.FirstOrDefault(c =>
            c["attributePath"]?.Value<string>() == "meterreading_max");
        maxCell.Should().NotBeNull();
        var maxValue = maxCell!["value"]?.Value<int>() ?? 0;
        maxValue.Should().BeGreaterThan(0);

        // Min should be less than or equal to max
        minValue.Should().BeLessThanOrEqualTo(maxValue);
    }

    [Fact]
    public async Task TransientAggregationQuery_WithFieldFilter_ReturnsFilteredAggregation()
    {
        // Arrange - Filter to only metering points with maintenance status
        var query = """
            query {
              runtime {
                transientQuery {
                  aggregation(
                    ckId: "AssetRepositoryIntegrationTest/MeteringPoint"
                    columnPaths: [
                      { attributePath: "meterReading", aggregationType: COUNT }
                    ]
                    fieldFilter: [
                      {
                        attributePath: "operatingStatus"
                        comparisonValue: 2
                        operator: EQUALS
                      }
                    ]
                  ) {
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
            }
            """;

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var cells = answer.SelectToken("data.runtime.transientQuery.aggregation.items[0].rows.items[0].cells.items");
        cells.Should().NotBeNull();

        var countCell = ((JArray)cells!).FirstOrDefault();
        countCell.Should().NotBeNull();

        // Only 1 metering point with Maintenance status (operatingStatus = 2)
        var countValue = countCell!["value"]?.Value<int>();
        countValue.Should().Be(1);
    }

    [Fact]
    public async Task TransientAggregationQuery_MultipleAggregations_ReturnsAllAggregations()
    {
        // Arrange
        var query = """
            query {
              runtime {
                transientQuery {
                  aggregation(
                    ckId: "AssetRepositoryIntegrationTest/MeteringPoint"
                    columnPaths: [
                      { attributePath: "meterReading", aggregationType: COUNT },
                      { attributePath: "meterReading", aggregationType: SUM },
                      { attributePath: "meterReading", aggregationType: AVERAGE },
                      { attributePath: "meterReading", aggregationType: MINIMUM },
                      { attributePath: "meterReading", aggregationType: MAXIMUM }
                    ]
                  ) {
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
            }
            """;

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        // Verify columns
        var columns = answer.SelectToken("data.runtime.transientQuery.aggregation.items[0].columns");
        columns.Should().NotBeNull();
        var columnArray = (JArray)columns!;
        columnArray.Should().HaveCount(5);

        // Verify cells
        var cells = answer.SelectToken("data.runtime.transientQuery.aggregation.items[0].rows.items[0].cells.items");
        cells.Should().NotBeNull();
        var cellArray = (JArray)cells!;
        cellArray.Should().HaveCount(5);
    }

    #endregion

    #region Grouping Aggregation Transient Queries

    [Fact]
    public async Task TransientGroupingAggregationQuery_GroupByOperatingStatus_ReturnsGroupedResults()
    {
        // Arrange
        var query = """
            query {
              runtime {
                transientQuery {
                  groupingAggregation(
                    ckId: "AssetRepositoryIntegrationTest/MeteringPoint"
                    groupByColumnPaths: ["operatingStatus"]
                    columnPaths: [
                      { attributePath: "meterReading", aggregationType: COUNT }
                    ]
                  ) {
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
            }
            """;

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        // Verify columns include both groupBy column and aggregation column
        var columns = answer.SelectToken("data.runtime.transientQuery.groupingAggregation.items[0].columns");
        columns.Should().NotBeNull();
        var columnArray = (JArray)columns!;
        columnArray.Should().HaveCount(2); // operatingStatus (groupBy) + meterReading (aggregation)

        // Verify we have grouped rows (one per distinct operatingStatus value)
        var rows = answer.SelectToken("data.runtime.transientQuery.groupingAggregation.items[0].rows.items");
        rows.Should().NotBeNull();
        var rowArray = (JArray)rows!;
        rowArray.Count.Should().BeGreaterThan(0);

        // Verify first row has both groupBy key and aggregation value as cells
        var firstRowCells = rowArray[0].SelectToken("cells.items");
        firstRowCells.Should().NotBeNull();
        var cellArray = (JArray)firstRowCells!;
        cellArray.Should().HaveCount(2); // operatingStatus key + meterReading count

        // First cell should be the groupBy key (operatingStatus) emitted as wire-form key
        var groupByCell = cellArray[0];
        groupByCell["attributePath"]?.Value<string>().Should().Be("operatingstatus");
        groupByCell["value"].Should().NotBeNull();

        // Second cell should be the aggregation value (COUNT(meterReading))
        var aggregationCell = cellArray[1];
        aggregationCell["attributePath"]?.Value<string>().Should().Be("meterreading_count");
        aggregationCell["value"].Should().NotBeNull();
    }

    [Fact]
    public async Task TransientGroupingAggregationQuery_GroupByWithMultipleAggregations_ReturnsAllAggregations()
    {
        // Arrange
        var query = """
            query {
              runtime {
                transientQuery {
                  groupingAggregation(
                    ckId: "AssetRepositoryIntegrationTest/MeteringPoint"
                    groupByColumnPaths: ["operatingStatus"]
                    columnPaths: [
                      { attributePath: "meterReading", aggregationType: COUNT },
                      { attributePath: "meterReading", aggregationType: SUM },
                      { attributePath: "meterReading", aggregationType: AVERAGE }
                    ]
                  ) {
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
            }
            """;

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        // Verify columns: 1 groupBy + 3 aggregations = 4 columns
        var columns = answer.SelectToken("data.runtime.transientQuery.groupingAggregation.items[0].columns");
        columns.Should().NotBeNull();
        var columnArray = (JArray)columns!;
        columnArray.Should().HaveCount(4);

        // Verify first row has 4 cells (1 groupBy key + 3 aggregation values)
        var firstRowCells = answer.SelectToken("data.runtime.transientQuery.groupingAggregation.items[0].rows.items[0].cells.items");
        firstRowCells.Should().NotBeNull();
        var cellArray = (JArray)firstRowCells!;
        cellArray.Should().HaveCount(4);
    }

    [Fact]
    public async Task TransientGroupingAggregationQuery_WithFieldFilter_ReturnsFilteredGroupedResults()
    {
        // Arrange - Only include active metering points (operatingStatus = 1)
        var query = """
            query {
              runtime {
                transientQuery {
                  groupingAggregation(
                    ckId: "AssetRepositoryIntegrationTest/MeteringPoint"
                    groupByColumnPaths: ["operatingStatus"]
                    columnPaths: [
                      { attributePath: "meterReading", aggregationType: COUNT }
                    ]
                    fieldFilter: [
                      {
                        attributePath: "operatingStatus"
                        comparisonValue: 1
                        operator: EQUALS
                      }
                    ]
                  ) {
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
            }
            """;

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        // Should only have one group (operatingStatus = 1)
        var rows = answer.SelectToken("data.runtime.transientQuery.groupingAggregation.items[0].rows.items");
        rows.Should().NotBeNull();
        var rowArray = (JArray)rows!;
        rowArray.Should().HaveCount(1);

        // Verify the group key is 1 (Active)
        var groupByCell = rowArray[0].SelectToken("cells.items[0]");
        groupByCell.Should().NotBeNull();
        groupByCell!["attributePath"]?.Value<string>().Should().Be("operatingstatus");
        // The value could be either 1 (integer) or "Active" (resolved enum name)
        groupByCell["value"].Should().NotBeNull();
    }

    [Fact]
    public async Task TransientGroupingAggregationQuery_GroupByMultipleColumns_ReturnsMultiKeyGroups()
    {
        // Arrange - Group by both operatingStatus and a constant path
        var query = """
            query {
              runtime {
                transientQuery {
                  groupingAggregation(
                    ckId: "AssetRepositoryIntegrationTest/Customer"
                    groupByColumnPaths: ["city"]
                    columnPaths: [
                      { attributePath: "firstName", aggregationType: COUNT }
                    ]
                  ) {
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
            }
            """;

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        // Verify we have groups by city
        var rows = answer.SelectToken("data.runtime.transientQuery.groupingAggregation.items[0].rows.items");
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

            // First cell should be city (groupBy key, wire-form)
            var cityCell = cellArray[0];
            cityCell["attributePath"]?.Value<string>().Should().Be("city");

            // Second cell should be COUNT(firstName) (wire-form with function suffix)
            var countCell = cellArray[1];
            countCell["attributePath"]?.Value<string>().Should().Be("firstname_count");
            countCell["value"]?.Value<int>().Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task TransientGroupingAggregationQuery_SumByGroup_ReturnsSumPerGroup()
    {
        // Arrange
        var query = """
            query {
              runtime {
                transientQuery {
                  groupingAggregation(
                    ckId: "AssetRepositoryIntegrationTest/MeteringPoint"
                    groupByColumnPaths: ["operatingStatus"]
                    columnPaths: [
                      { attributePath: "meterReading", aggregationType: SUM }
                    ]
                  ) {
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
            }
            """;

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var rows = answer.SelectToken("data.runtime.transientQuery.groupingAggregation.items[0].rows.items");
        rows.Should().NotBeNull();
        var rowArray = (JArray)rows!;
        rowArray.Count.Should().BeGreaterThan(0);

        // Verify each group has a sum value
        foreach (var row in rowArray)
        {
            var sumCell = row.SelectToken("cells.items[1]");
            sumCell.Should().NotBeNull();
            sumCell!["attributePath"]?.Value<string>().Should().Be("meterreading_sum");
            // Sum should be a number (could be 0 for some groups)
            sumCell["value"].Should().NotBeNull();
        }
    }

    #endregion

    #region Error Cases

    [Fact]
    public async Task TransientSimpleQuery_InvalidColumnPath_ReturnsError()
    {
        // Arrange
        var query = """
            query {
              runtime {
                transientQuery {
                  simple(
                    ckId: "AssetRepositoryIntegrationTest/MeteringPoint"
                    columnPaths: ["invalidColumnPath"]
                  ) {
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
            }
            """;

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TransientSimpleQuery_InvalidCkTypeId_ReturnsError()
    {
        // Arrange
        var query = """
            query {
              runtime {
                transientQuery {
                  simple(
                    ckId: "InvalidNamespace/InvalidType"
                    columnPaths: ["rtWellKnownName"]
                  ) {
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
            }
            """;

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().NotBeNullOrEmpty();
    }

    #endregion
}
