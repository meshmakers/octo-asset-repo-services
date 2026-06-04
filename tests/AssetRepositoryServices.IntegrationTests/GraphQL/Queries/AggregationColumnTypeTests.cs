using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
/// Tests that aggregation columns report the correct attributeValueType based on the aggregation operation,
/// not the source column type.
/// </summary>
[Collection("Sequential")]
public class AggregationColumnTypeTests : IClassFixture<GraphQlTestFixture>
{
    private readonly GraphQlTestFixture _fixture;

    public AggregationColumnTypeTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    #region COUNT Aggregation Type Tests

    [Fact]
    public async Task AggregationQuery_CountOnIntegerColumn_ReturnsIntegerType()
    {
        // Arrange - COUNT on MeterReading (Integer) should return INTEGER type
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
                      columns {
                        attributePath
                        attributeValueType
                        aggregationType
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

        var columns = answer.SelectToken("data.runtime.transientQuery.aggregation.items[0].columns");
        columns.Should().NotBeNull();
        var columnArray = (JArray)columns!;
        columnArray.Should().HaveCount(1);

        var countColumn = columnArray[0];
        countColumn["aggregationType"]?.Value<string>().Should().Be("COUNT");
        countColumn["attributeValueType"]?.Value<string>().Should().Be("INTEGER",
            "COUNT aggregation always returns INTEGER regardless of source type");
    }

    [Fact]
    public async Task AggregationQuery_CountOnStringColumn_ReturnsIntegerType()
    {
        // Arrange - COUNT on FirstName (String) should return INTEGER type
        var query = """
            query {
              runtime {
                transientQuery {
                  aggregation(
                    ckId: "AssetRepositoryIntegrationTest/Customer"
                    columnPaths: [
                      { attributePath: "firstName", aggregationType: COUNT }
                    ]
                  ) {
                    items {
                      columns {
                        attributePath
                        attributeValueType
                        aggregationType
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

        var columns = answer.SelectToken("data.runtime.transientQuery.aggregation.items[0].columns");
        columns.Should().NotBeNull();
        var columnArray = (JArray)columns!;
        columnArray.Should().HaveCount(1);

        var countColumn = columnArray[0];
        countColumn["aggregationType"]?.Value<string>().Should().Be("COUNT");
        countColumn["attributeValueType"]?.Value<string>().Should().Be("INTEGER",
            "COUNT aggregation always returns INTEGER regardless of source type");
    }

    #endregion

    #region SUM Aggregation Type Tests

    [Fact]
    public async Task AggregationQuery_SumOnIntegerColumn_ReturnsIntegerType()
    {
        // Arrange - SUM on MeterReading (Integer) should return INTEGER type
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
                      columns {
                        attributePath
                        attributeValueType
                        aggregationType
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

        var columns = answer.SelectToken("data.runtime.transientQuery.aggregation.items[0].columns");
        columns.Should().NotBeNull();
        var columnArray = (JArray)columns!;
        columnArray.Should().HaveCount(1);

        var sumColumn = columnArray[0];
        sumColumn["aggregationType"]?.Value<string>().Should().Be("SUM");
        sumColumn["attributeValueType"]?.Value<string>().Should().Be("INTEGER",
            "SUM on INTEGER source should return INTEGER");
    }

    [Fact]
    public async Task AggregationQuery_SumOnDoubleColumn_ReturnsDoubleType()
    {
        // Arrange - SUM on ReadingValue (Double) should return DOUBLE type
        var query = """
            query {
              runtime {
                transientQuery {
                  aggregation(
                    ckId: "AssetRepositoryIntegrationTest/VehicleReading"
                    columnPaths: [
                      { attributePath: "readingValue", aggregationType: SUM }
                    ]
                  ) {
                    items {
                      columns {
                        attributePath
                        attributeValueType
                        aggregationType
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

        var columns = answer.SelectToken("data.runtime.transientQuery.aggregation.items[0].columns");
        columns.Should().NotBeNull();
        var columnArray = (JArray)columns!;
        columnArray.Should().HaveCount(1);

        var sumColumn = columnArray[0];
        sumColumn["aggregationType"]?.Value<string>().Should().Be("SUM");
        sumColumn["attributeValueType"]?.Value<string>().Should().Be("DOUBLE",
            "SUM on DOUBLE source should return DOUBLE");
    }

    #endregion

    #region AVERAGE Aggregation Type Tests

    [Fact]
    public async Task AggregationQuery_AverageOnIntegerColumn_ReturnsDoubleType()
    {
        // Arrange - AVERAGE on MeterReading (Integer) should return DOUBLE type
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
                      columns {
                        attributePath
                        attributeValueType
                        aggregationType
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

        var columns = answer.SelectToken("data.runtime.transientQuery.aggregation.items[0].columns");
        columns.Should().NotBeNull();
        var columnArray = (JArray)columns!;
        columnArray.Should().HaveCount(1);

        var avgColumn = columnArray[0];
        avgColumn["aggregationType"]?.Value<string>().Should().Be("AVERAGE");
        avgColumn["attributeValueType"]?.Value<string>().Should().Be("DOUBLE",
            "AVERAGE always returns DOUBLE regardless of source type");
    }

    [Fact]
    public async Task AggregationQuery_AverageOnDoubleColumn_ReturnsDoubleType()
    {
        // Arrange - AVERAGE on ReadingValue (Double) should return DOUBLE type
        var query = """
            query {
              runtime {
                transientQuery {
                  aggregation(
                    ckId: "AssetRepositoryIntegrationTest/VehicleReading"
                    columnPaths: [
                      { attributePath: "readingValue", aggregationType: AVERAGE }
                    ]
                  ) {
                    items {
                      columns {
                        attributePath
                        attributeValueType
                        aggregationType
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

        var columns = answer.SelectToken("data.runtime.transientQuery.aggregation.items[0].columns");
        columns.Should().NotBeNull();
        var columnArray = (JArray)columns!;
        columnArray.Should().HaveCount(1);

        var avgColumn = columnArray[0];
        avgColumn["aggregationType"]?.Value<string>().Should().Be("AVERAGE");
        avgColumn["attributeValueType"]?.Value<string>().Should().Be("DOUBLE",
            "AVERAGE always returns DOUBLE regardless of source type");
    }

    #endregion

    #region MIN/MAX Aggregation Type Tests

    [Fact]
    public async Task AggregationQuery_MinOnIntegerColumn_PreservesIntegerType()
    {
        // Arrange - MINIMUM on MeterReading (Integer) should preserve INTEGER type
        var query = """
            query {
              runtime {
                transientQuery {
                  aggregation(
                    ckId: "AssetRepositoryIntegrationTest/MeteringPoint"
                    columnPaths: [
                      { attributePath: "meterReading", aggregationType: MINIMUM }
                    ]
                  ) {
                    items {
                      columns {
                        attributePath
                        attributeValueType
                        aggregationType
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

        var columns = answer.SelectToken("data.runtime.transientQuery.aggregation.items[0].columns");
        columns.Should().NotBeNull();
        var columnArray = (JArray)columns!;
        columnArray.Should().HaveCount(1);

        var minColumn = columnArray[0];
        minColumn["aggregationType"]?.Value<string>().Should().Be("MINIMUM");
        minColumn["attributeValueType"]?.Value<string>().Should().Be("INTEGER",
            "MINIMUM should preserve source type (INTEGER)");
    }

    [Fact]
    public async Task AggregationQuery_MaxOnDoubleColumn_PreservesDoubleType()
    {
        // Arrange - MAXIMUM on ReadingValue (Double) should preserve DOUBLE type
        var query = """
            query {
              runtime {
                transientQuery {
                  aggregation(
                    ckId: "AssetRepositoryIntegrationTest/VehicleReading"
                    columnPaths: [
                      { attributePath: "readingValue", aggregationType: MAXIMUM }
                    ]
                  ) {
                    items {
                      columns {
                        attributePath
                        attributeValueType
                        aggregationType
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

        var columns = answer.SelectToken("data.runtime.transientQuery.aggregation.items[0].columns");
        columns.Should().NotBeNull();
        var columnArray = (JArray)columns!;
        columnArray.Should().HaveCount(1);

        var maxColumn = columnArray[0];
        maxColumn["aggregationType"]?.Value<string>().Should().Be("MAXIMUM");
        maxColumn["attributeValueType"]?.Value<string>().Should().Be("DOUBLE",
            "MAXIMUM should preserve source type (DOUBLE)");
    }

    [Fact]
    public async Task AggregationQuery_MinMaxPreserveSourceTypes()
    {
        // Arrange - Combined test for MIN on Integer and MAX on Double
        var queryInt = """
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
                      columns {
                        attributePath
                        attributeValueType
                        aggregationType
                      }
                    }
                  }
                }
              }
            }
            """;

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(queryInt);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var columns = answer.SelectToken("data.runtime.transientQuery.aggregation.items[0].columns");
        columns.Should().NotBeNull();
        var columnArray = (JArray)columns!;
        columnArray.Should().HaveCount(2);

        // Both MIN and MAX on Integer should return INTEGER
        var minColumn = columnArray.First(c => c["aggregationType"]?.Value<string>() == "MINIMUM");
        minColumn["attributeValueType"]?.Value<string>().Should().Be("INTEGER",
            "MINIMUM should preserve INTEGER source type");

        var maxColumn = columnArray.First(c => c["aggregationType"]?.Value<string>() == "MAXIMUM");
        maxColumn["attributeValueType"]?.Value<string>().Should().Be("INTEGER",
            "MAXIMUM should preserve INTEGER source type");
    }

    #endregion

    #region GroupBy Column Type Tests

    [Fact]
    public async Task GroupingAggregationQuery_GroupByColumnPreservesSourceType_AggregationColumnReturnsCorrectType()
    {
        // Arrange - GroupBy on Enum, COUNT on Integer
        // GroupBy column should preserve ENUM type, COUNT should return INTEGER
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
                      columns {
                        attributePath
                        attributeValueType
                        aggregationType
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

        var columns = answer.SelectToken("data.runtime.transientQuery.groupingAggregation.items[0].columns");
        columns.Should().NotBeNull();
        var columnArray = (JArray)columns!;
        columnArray.Should().HaveCount(2); // groupBy column + aggregation column

        // First column should be the groupBy column (operatingStatus, emitted as wire-form key)
        var groupByColumn = columnArray.First(c => c["attributePath"]?.Value<string>() == "operatingstatus");
        groupByColumn.Should().NotBeNull();
        groupByColumn["aggregationType"]?.Value<string>().Should().Be("NONE",
            "GroupBy column should have NONE aggregation type");
        groupByColumn["attributeValueType"]?.Value<string>().Should().Be("ENUM",
            "GroupBy column should preserve ENUM source type");

        // Second column should be the aggregation column (meterReading COUNT)
        var countColumn = columnArray.First(c => c["aggregationType"]?.Value<string>() == "COUNT");
        countColumn.Should().NotBeNull();
        countColumn["attributeValueType"]?.Value<string>().Should().Be("INTEGER",
            "COUNT aggregation should return INTEGER type");
    }

    [Fact]
    public async Task GroupingAggregationQuery_GroupByStringColumn_PreservesStringType()
    {
        // Arrange - GroupBy on String (City), SUM on Integer
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
                        attributeValueType
                        aggregationType
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

        var columns = answer.SelectToken("data.runtime.transientQuery.groupingAggregation.items[0].columns");
        columns.Should().NotBeNull();
        var columnArray = (JArray)columns!;
        columnArray.Should().HaveCount(2);

        // GroupBy column (city) should preserve STRING type — wire-form is same as original (single segment, already lowercase)
        var groupByColumn = columnArray.First(c => c["attributePath"]?.Value<string>() == "city");
        groupByColumn.Should().NotBeNull();
        groupByColumn["aggregationType"]?.Value<string>().Should().Be("NONE");
        groupByColumn["attributeValueType"]?.Value<string>().Should().Be("STRING",
            "GroupBy column should preserve STRING source type");

        // COUNT column should return INTEGER
        var countColumn = columnArray.First(c => c["aggregationType"]?.Value<string>() == "COUNT");
        countColumn.Should().NotBeNull();
        countColumn["attributeValueType"]?.Value<string>().Should().Be("INTEGER");
    }

    #endregion

    #region Multiple Aggregations Mixed Types

    [Fact]
    public async Task AggregationQuery_MultipleAggregationTypes_ReturnsCorrectTypesForEach()
    {
        // Arrange - Multiple aggregations on the same Integer column
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
                        attributeValueType
                        aggregationType
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

        var columns = answer.SelectToken("data.runtime.transientQuery.aggregation.items[0].columns");
        columns.Should().NotBeNull();
        var columnArray = (JArray)columns!;
        columnArray.Should().HaveCount(5);

        // COUNT -> INTEGER
        var countColumn = columnArray.First(c => c["aggregationType"]?.Value<string>() == "COUNT");
        countColumn["attributeValueType"]?.Value<string>().Should().Be("INTEGER");

        // SUM on INTEGER -> INTEGER
        var sumColumn = columnArray.First(c => c["aggregationType"]?.Value<string>() == "SUM");
        sumColumn["attributeValueType"]?.Value<string>().Should().Be("INTEGER");

        // AVERAGE -> DOUBLE (always)
        var avgColumn = columnArray.First(c => c["aggregationType"]?.Value<string>() == "AVERAGE");
        avgColumn["attributeValueType"]?.Value<string>().Should().Be("DOUBLE");

        // MINIMUM on INTEGER -> INTEGER
        var minColumn = columnArray.First(c => c["aggregationType"]?.Value<string>() == "MINIMUM");
        minColumn["attributeValueType"]?.Value<string>().Should().Be("INTEGER");

        // MAXIMUM on INTEGER -> INTEGER
        var maxColumn = columnArray.First(c => c["aggregationType"]?.Value<string>() == "MAXIMUM");
        maxColumn["attributeValueType"]?.Value<string>().Should().Be("INTEGER");
    }

    #endregion
}
