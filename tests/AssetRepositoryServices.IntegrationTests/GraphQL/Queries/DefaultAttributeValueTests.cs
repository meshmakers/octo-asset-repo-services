using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using System.Text.Json;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

/// <summary>
/// Integration tests for handling attributes with default values.
/// Tests the scenario where an entity was created before an attribute with default value was added.
/// This reproduces the bug AB#3306 where querying lastSyncedSequenceNumber returns
/// "Cannot return null for a non-null type" error.
/// </summary>
[Collection("Sequential")]
public class DefaultAttributeValueTests : IClassFixture<GraphQlTestFixture>
{
    private readonly GraphQlTestFixture _fixture;
    private const string MeteringPointCkTypeId = "AssetRepositoryIntegrationTest/MeteringPoint";

    public DefaultAttributeValueTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    /// <summary>
    /// Tests that querying an attribute with a default value returns the default when the attribute
    /// is missing from the MongoDB document. This simulates legacy data that was created before
    /// the attribute was added to the schema.
    ///
    /// Bug AB#3306: When an attribute has a default value but is not marked as optional,
    /// GraphQL treats it as non-nullable (Int!). If the entity was created without this field
    /// (e.g., created before the field was added), MongoDB returns null, causing GraphQL to fail
    /// with "Cannot return null for a non-null type".
    ///
    /// Expected behavior: The default value (0) should be returned instead of null.
    /// </summary>
    [Fact]
    public async Task Query_AttributeWithDefaultValue_WhenMissingInMongoDB_ShouldReturnDefaultValue()
    {
        // Arrange - Create an entity with all fields including syncSequenceNumber
        var createMutation = """
            mutation ($entities: [RtEntityInput!]!) {
                runtime {
                    runtimeEntities {
                        create(entities: $entities) {
                            rtId
                            ckTypeId
                        }
                    }
                }
            }
            """;

        var createVariables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = MeteringPointCkTypeId,
                    rtWellKnownName = "TestMeteringPoint_DefaultValueTest",
                    attributes = new object[]
                    {
                        new { attributeName = "meteringPointNumber", value = "AT_TEST_DEFAULT_001" },
                        new { attributeName = "meterReading", value = 100 },
                        new { attributeName = "operatingStatus", value = 1 },
                        new { attributeName = "name", value = "Test MeteringPoint for Default Value" },
                        new { attributeName = "description", value = "Testing default value handling" },
                        new { attributeName = "syncSequenceNumber", value = 5 }
                    }
                }
            }
        });

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation, createVariables);
        createResult.Errors.Should().BeNullOrEmpty("Entity creation should succeed");

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);
        var rtId = createAnswer.SelectToken("data.runtime.runtimeEntities.create[0].rtId")?.Value<string>();
        rtId.Should().NotBeNullOrEmpty();

        try
        {
            // Remove the syncSequenceNumber field from MongoDB to simulate legacy data
            // This is what happens when an entity was created before the field was added to the schema
            // MeteringPoint inherits from Asset, so it's stored in RtEntity_AssetRepositoryIntegrationTestAsset
            await _fixture.RemoveAttributeFromMongoDb(rtId!, "syncSequenceNumber", "AssetRepositoryIntegrationTestAsset");

            // Act - Query the entity and request the syncSequenceNumber field
            // This should return the default value (0) instead of failing with null error
            var query = $$"""
                query {
                    runtime {
                        assetRepositoryIntegrationTestMeteringPoint(rtId: "{{rtId}}") {
                            items {
                                rtId
                                name
                                syncSequenceNumber
                            }
                        }
                    }
                }
                """;

            var result = await _fixture.ExecuteGraphQlAsync(query);

            // Assert - The query should succeed and return the default value
            // If this test fails with "Cannot return null for a non-null type",
            // it confirms bug AB#3306 is present
            result.Should().NotBeNull();
            result.Errors.Should().BeNullOrEmpty(
                "Querying an attribute with default value should not fail. " +
                "If it fails with 'Cannot return null for a non-null type', bug AB#3306 is present.");

            var json = _fixture.SerializeGraphQl(result);
            var answer = JObject.Parse(json);

            var items = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestMeteringPoint.items") as JArray;
            items.Should().NotBeNull();
            items.Should().HaveCount(1);

            var entity = items![0];
            entity["rtId"]?.Value<string>().Should().Be(rtId);

            // The syncSequenceNumber should return the default value (0)
            var syncSequenceNumber = entity["syncSequenceNumber"]?.Value<int>();
            syncSequenceNumber.Should().Be(0, "Default value of 0 should be returned when attribute is missing from MongoDB");
        }
        finally
        {
            // Cleanup - Delete the test entity
            await DeleteEntity(rtId!);
        }
    }

    /// <summary>
    /// Tests that when an attribute with default value IS explicitly set, the set value is returned.
    /// </summary>
    [Fact]
    public async Task Query_AttributeWithDefaultValue_WhenExplicitlySet_ShouldReturnSetValue()
    {
        // Arrange - Create an entity WITH the syncSequenceNumber field set
        var createMutation = """
            mutation ($entities: [RtEntityInput!]!) {
                runtime {
                    runtimeEntities {
                        create(entities: $entities) {
                            rtId
                            ckTypeId
                        }
                    }
                }
            }
            """;

        var createVariables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = MeteringPointCkTypeId,
                    rtWellKnownName = "TestMeteringPoint_ExplicitValueTest",
                    attributes = new object[]
                    {
                        new { attributeName = "meteringPointNumber", value = "AT_TEST_EXPLICIT_001" },
                        new { attributeName = "meterReading", value = 200 },
                        new { attributeName = "operatingStatus", value = 1 },
                        new { attributeName = "name", value = "Test MeteringPoint for Explicit Value" },
                        new { attributeName = "description", value = "Testing explicit value handling" },
                        new { attributeName = "syncSequenceNumber", value = 42 }
                    }
                }
            }
        });

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation, createVariables);
        createResult.Errors.Should().BeNullOrEmpty("Entity creation should succeed");

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);
        var rtId = createAnswer.SelectToken("data.runtime.runtimeEntities.create[0].rtId")?.Value<string>();
        rtId.Should().NotBeNullOrEmpty();

        try
        {
            // Act - Query the entity and request the syncSequenceNumber field
            var query = $$"""
                query {
                    runtime {
                        assetRepositoryIntegrationTestMeteringPoint(rtId: "{{rtId}}") {
                            items {
                                rtId
                                name
                                syncSequenceNumber
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

            var items = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestMeteringPoint.items") as JArray;
            items.Should().NotBeNull();
            items.Should().HaveCount(1);

            var entity = items![0];
            entity["rtId"]?.Value<string>().Should().Be(rtId);

            // The syncSequenceNumber should return the explicitly set value
            var syncSequenceNumber = entity["syncSequenceNumber"]?.Value<int>();
            syncSequenceNumber.Should().Be(42, "Explicitly set value should be returned");
        }
        finally
        {
            // Cleanup
            await DeleteEntity(rtId!);
        }
    }

    private async Task DeleteEntity(string rtId)
    {
        var deleteMutation = $$"""
            mutation {
                runtime {
                    runtimeEntities {
                        delete(entities: [{ rtId: "{{rtId}}", ckTypeId: "{{MeteringPointCkTypeId}}" }])
                    }
                }
            }
            """;

        await _fixture.ExecuteGraphQlAsync(deleteMutation);
    }
}
