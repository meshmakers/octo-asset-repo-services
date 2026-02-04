using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using System.Text.Json;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Mutations;

/// <summary>
/// Integration tests for LargeBinary scalar type (used for BinaryLinked attributes).
/// Tests the parsing of byte arrays sent as JSON arrays.
/// </summary>
[Collection("Sequential")]
public class LargeBinaryScalarTests : IClassFixture<GraphQlTestFixture>
{
    private readonly GraphQlTestFixture _fixture;

    // CkTypeId for testing
    private const string ProductCkTypeId = "AssetRepositoryIntegrationTest/Product";

    public LargeBinaryScalarTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    /// <summary>
    /// This test verifies that BinaryLinked attributes can accept byte arrays sent as JSON arrays
    /// when using the type-specific mutation endpoint.
    /// Bug: LargeBinary scalar type cannot parse byte arrays, resulting in:
    /// "Unable to convert '(array)' to 'LargeBinary'"
    /// </summary>
    [Fact]
    public async Task Create_WithBinaryLinkedAttributeAsJsonArray_TypeSpecificMutation_ShouldSucceed()
    {
        // Arrange - Create entity using type-specific mutation with BinaryLinked attribute
        // The type-specific mutation uses typed input which triggers LargeBinaryDtoType.ParseValue
        var mutation = @"
            mutation ($entities: [AssetRepositoryIntegrationTestProductInput!]!) {
                runtime {
                    assetRepositoryIntegrationTestProducts {
                        create(entities: $entities) {
                            rtId
                            productName
                            productCode
                        }
                    }
                }
            }";

        // Sample binary data - PNG header bytes
        // When serialized to JSON, this becomes [137, 80, 78, 71, 13, 10, 26, 10]
        var binaryData = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        var variables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    rtWellKnownName = "TestProduct_BinaryLinkedFromArray_TypeSpecific",
                    productName = "Product with BinaryLinked Image",
                    productCode = "PBLJ-001",
                    // BinaryLinked attribute with byte array - this triggers the bug
                    productImage = binaryData
                }
            }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(mutation, variables);

        // Assert
        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result: {json}");

        // Currently this fails with: "Unable to convert '(array)' to 'LargeBinary'"
        // After fix, it should succeed
        result.Errors.Should().BeNullOrEmpty("BinaryLinked attributes should accept byte arrays from JSON");
        result.Data.Should().NotBeNull();

        var answer = JObject.Parse(json);
        var createdEntities = answer.SelectToken("data.runtime.assetRepositoryIntegrationTestProducts.create") as JArray;
        createdEntities.Should().NotBeNull();
        createdEntities.Should().HaveCount(1);

        var createdEntity = createdEntities![0];
        createdEntity["rtId"].Should().NotBeNull();
    }

    /// <summary>
    /// Test with generic mutation to ensure it still works (for comparison).
    /// </summary>
    [Fact]
    public async Task Create_WithBinaryLinkedAttributeAsJsonArray_GenericMutation_ShouldSucceed()
    {
        // Arrange - Create entity with BinaryLinked attribute using generic mutation
        // Generic mutation uses SimpleScalarType which accepts any value
        var mutation = @"
            mutation ($entities: [RtEntityInput!]!) {
                runtime {
                    runtimeEntities {
                        create(entities: $entities) {
                            rtId
                            ckTypeId
                            attributes(first: 20) {
                                items {
                                    attributeName
                                    value
                                }
                            }
                        }
                    }
                }
            }";

        // Sample binary data - PNG header bytes
        var binaryData = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        var variables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = ProductCkTypeId,
                    rtWellKnownName = "TestProduct_BinaryLinkedFromArray_Generic",
                    attributes = new object[]
                    {
                        new { attributeName = "productName", value = "Product with BinaryLinked Image" },
                        new { attributeName = "productCode", value = "PBLJ-002" },
                        // BinaryLinked attribute with byte array
                        new { attributeName = "productImage", value = binaryData }
                    }
                }
            }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(mutation, variables);

        // Assert
        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result: {json}");

        // Generic mutation should work (uses SimpleScalarType)
        result.Errors.Should().BeNullOrEmpty("Generic mutation should accept byte arrays");
        result.Data.Should().NotBeNull();

        var answer = JObject.Parse(json);
        var createdEntities = answer.SelectToken("data.runtime.runtimeEntities.create") as JArray;
        createdEntities.Should().NotBeNull();
        createdEntities.Should().HaveCount(1);

        var createdEntity = createdEntities![0];
        createdEntity["rtId"].Should().NotBeNull();
    }

    /// <summary>
    /// Tests that BinaryLinked attributes with larger byte arrays work correctly
    /// using type-specific mutation.
    /// </summary>
    [Fact]
    public async Task Create_WithLargerBinaryLinkedData_TypeSpecificMutation_ShouldSucceed()
    {
        // Arrange
        var mutation = @"
            mutation ($entities: [AssetRepositoryIntegrationTestProductInput!]!) {
                runtime {
                    assetRepositoryIntegrationTestProducts {
                        create(entities: $entities) {
                            rtId
                            productName
                        }
                    }
                }
            }";

        // Create a 1KB binary data block
        var binaryData = new byte[1024];
        for (int i = 0; i < binaryData.Length; i++)
        {
            binaryData[i] = (byte)(i % 256);
        }

        var variables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    rtWellKnownName = "TestProduct_LargeBinaryLinked_TypeSpecific",
                    productName = "Product with Large BinaryLinked",
                    productCode = "PLBL-001",
                    productImage = binaryData
                }
            }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(mutation, variables);

        // Assert
        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result: {json}");

        result.Errors.Should().BeNullOrEmpty("Larger BinaryLinked data should be supported");
        result.Data.Should().NotBeNull();
    }

    /// <summary>
    /// Tests that BinaryLinked attributes with empty byte arrays work correctly
    /// using type-specific mutation.
    /// </summary>
    [Fact]
    public async Task Create_WithEmptyBinaryLinkedData_TypeSpecificMutation_ShouldSucceed()
    {
        // Arrange
        var mutation = @"
            mutation ($entities: [AssetRepositoryIntegrationTestProductInput!]!) {
                runtime {
                    assetRepositoryIntegrationTestProducts {
                        create(entities: $entities) {
                            rtId
                            productName
                        }
                    }
                }
            }";

        var emptyBinaryData = Array.Empty<byte>();

        var variables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    rtWellKnownName = "TestProduct_EmptyBinaryLinked_TypeSpecific",
                    productName = "Product with Empty BinaryLinked",
                    productCode = "PEBL-001",
                    productImage = emptyBinaryData
                }
            }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(mutation, variables);

        // Assert
        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result: {json}");

        result.Errors.Should().BeNullOrEmpty("Empty BinaryLinked data should be supported");
        result.Data.Should().NotBeNull();
    }

    /// <summary>
    /// Tests updating an entity with BinaryLinked attribute using byte array
    /// with type-specific mutation.
    /// </summary>
    [Fact]
    public async Task Update_BinaryLinkedAttribute_TypeSpecificMutation_ShouldSucceed()
    {
        // Arrange - First create an entity without BinaryLinked
        var createMutation = @"
            mutation ($entities: [AssetRepositoryIntegrationTestProductInput!]!) {
                runtime {
                    assetRepositoryIntegrationTestProducts {
                        create(entities: $entities) {
                            rtId
                        }
                    }
                }
            }";

        var createVariables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    rtWellKnownName = "TestProduct_UpdateBinaryLinked_TypeSpecific",
                    productName = "Product to Update BinaryLinked",
                    productCode = "PUBL-001"
                }
            }
        });

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation, createVariables);
        var createJson = _fixture.SerializeGraphQl(createResult);
        _fixture.OutputHelper?.WriteLine($"Create Result: {createJson}");
        createResult.Errors.Should().BeNullOrEmpty();

        var createAnswer = JObject.Parse(createJson);
        var createdRtId = createAnswer.SelectToken("data.runtime.assetRepositoryIntegrationTestProducts.create[0].rtId")?.Value<string>();
        createdRtId.Should().NotBeNullOrEmpty();

        // Now update with BinaryLinked data using type-specific mutation
        // The update input type is named "{InputTypeName}Update" which becomes "AssetRepositoryIntegrationTestProductInputUpdate"
        var updateMutation = @"
            mutation ($entities: [AssetRepositoryIntegrationTestProductInputUpdate!]!) {
                runtime {
                    assetRepositoryIntegrationTestProducts {
                        update(entities: $entities) {
                            rtId
                            productName
                        }
                    }
                }
            }";

        var binaryData = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }; // JPEG header

        var updateVariables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    rtId = createdRtId,
                    item = new
                    {
                        productImage = binaryData
                    }
                }
            }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(updateMutation, updateVariables);

        // Assert
        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Update Result: {json}");

        result.Errors.Should().BeNullOrEmpty("Updating BinaryLinked with byte array should succeed");
        result.Data.Should().NotBeNull();
    }

    /// <summary>
    /// Tests that null values for BinaryLinked attributes work correctly
    /// using type-specific mutation.
    /// </summary>
    [Fact]
    public async Task Create_WithNullBinaryLinkedData_TypeSpecificMutation_ShouldSucceed()
    {
        // Arrange
        var mutation = @"
            mutation ($entities: [AssetRepositoryIntegrationTestProductInput!]!) {
                runtime {
                    assetRepositoryIntegrationTestProducts {
                        create(entities: $entities) {
                            rtId
                            productName
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    rtWellKnownName = "TestProduct_NullBinaryLinked_TypeSpecific",
                    productName = "Product with Null BinaryLinked",
                    productCode = "PNBL-001",
                    productImage = (byte[]?)null
                }
            }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(mutation, variables);

        // Assert
        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result: {json}");

        result.Errors.Should().BeNullOrEmpty("Null BinaryLinked data should be supported");
        result.Data.Should().NotBeNull();
    }
}
