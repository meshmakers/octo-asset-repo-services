using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using System.Text.Json;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Mutations;

/// <summary>
/// Integration tests for mutations with complex attribute types (Record, RecordArray, BinaryLinked).
/// Tests the GraphQL schema directly without HTTP.
/// Uses the AssetRepositoryIntegrationTest model with the Product type.
/// </summary>
[Collection("Sequential")]
public class RtEntityComplexAttributeMutationTests : IClassFixture<GraphQlTestFixture>
{
    private readonly GraphQlTestFixture _fixture;

    // CkTypeId for testing
    private const string ProductCkTypeId = "AssetRepositoryIntegrationTest/Product";

    public RtEntityComplexAttributeMutationTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    #region Record Attribute Tests

    [Fact]
    public async Task Create_WithRecordAttribute_ReturnsCreatedEntity()
    {
        // Arrange - Create entity with a Record attribute (MainSpecification)
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

        var variables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = ProductCkTypeId,
                    rtWellKnownName = "TestProduct_RecordAttribute",
                    attributes = new object[]
                    {
                        new { attributeName = "productName", value = "Industrial Sensor X100" },
                        new { attributeName = "productCode", value = "ISX-100" },
                        new
                        {
                            attributeName = "mainSpecification",
                            value = new
                            {
                                name = "Operating Temperature",
                                value = "-40 to +85",
                                unit = "°C"
                            }
                        }
                    }
                }
            }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(mutation, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result: {json}");
        var answer = JObject.Parse(json);

        var createdEntities = answer.SelectToken("data.runtime.runtimeEntities.create") as JArray;
        createdEntities.Should().NotBeNull();
        createdEntities.Should().HaveCount(1);

        var createdEntity = createdEntities![0];
        createdEntity["rtId"].Should().NotBeNull();
        createdEntity["ckTypeId"]?.Value<string>().Should().Contain("Product");

        var attributes = createdEntity["attributes"]?["items"] as JArray;
        attributes.Should().NotBeNull();

        // Verify the Record attribute is present
        var mainSpec = attributes!.FirstOrDefault(a => a["attributeName"]?.Value<string>() == "mainSpecification");
        mainSpec.Should().NotBeNull("mainSpecification attribute should be present");
        mainSpec!["value"].Should().NotBeNull();
    }

    [Fact]
    public async Task Create_WithMultipleRecordAttributes_ReturnsCreatedEntity()
    {
        // Arrange - Create entity with multiple Record attributes
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

        var variables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = ProductCkTypeId,
                    rtWellKnownName = "TestProduct_MultipleRecords",
                    attributes = new object[]
                    {
                        new { attributeName = "productName", value = "Temperature Controller TC-500" },
                        new { attributeName = "productCode", value = "TC-500" },
                        new
                        {
                            attributeName = "mainSpecification",
                            value = new
                            {
                                name = "Control Range",
                                value = "0-100",
                                unit = "%"
                            }
                        },
                        new
                        {
                            attributeName = "manufacturerContact",
                            value = new
                            {
                                name = "TechCorp Support",
                                email = "support@techcorp.com",
                                phone = "+1-555-0100"
                            }
                        }
                    }
                }
            }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(mutation, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result: {json}");
        var answer = JObject.Parse(json);

        var createdEntities = answer.SelectToken("data.runtime.runtimeEntities.create") as JArray;
        createdEntities.Should().NotBeNull();
        createdEntities.Should().HaveCount(1);

        var attributes = createdEntities![0]["attributes"]?["items"] as JArray;
        attributes.Should().NotBeNull();

        // Verify both Record attributes are present
        var mainSpec = attributes!.FirstOrDefault(a => a["attributeName"]?.Value<string>() == "mainSpecification");
        mainSpec.Should().NotBeNull("mainSpecification attribute should be present");

        var contact = attributes.FirstOrDefault(a => a["attributeName"]?.Value<string>() == "manufacturerContact");
        contact.Should().NotBeNull("manufacturerContact attribute should be present");
    }

    [Fact]
    public async Task Update_RecordAttribute_ReturnsUpdatedEntity()
    {
        // Arrange - First create an entity with a Record attribute
        var createMutation = @"
            mutation ($entities: [RtEntityInput!]!) {
                runtime {
                    runtimeEntities {
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
                    ckTypeId = ProductCkTypeId,
                    rtWellKnownName = "TestProduct_UpdateRecord",
                    attributes = new object[]
                    {
                        new { attributeName = "productName", value = "Pressure Gauge PG-200" },
                        new { attributeName = "productCode", value = "PG-200" },
                        new
                        {
                            attributeName = "mainSpecification",
                            value = new
                            {
                                name = "Pressure Range",
                                value = "0-10",
                                unit = "bar"
                            }
                        }
                    }
                }
            }
        });

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation, createVariables);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);
        var createdRtId = createAnswer.SelectToken("data.runtime.runtimeEntities.create[0].rtId")?.Value<string>();
        createdRtId.Should().NotBeNullOrEmpty();

        // Now update the Record attribute
        var updateMutation = @"
            mutation ($entities: [RtEntityUpdate!]!) {
                runtime {
                    runtimeEntities {
                        update(entities: $entities) {
                            rtId
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

        var updateVariables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    rtId = createdRtId,
                    item = new
                    {
                        ckTypeId = ProductCkTypeId,
                        attributes = new object[]
                        {
                            new
                            {
                                attributeName = "mainSpecification",
                                value = new
                                {
                                    name = "Extended Pressure Range",
                                    value = "0-16",
                                    unit = "bar"
                                }
                            }
                        }
                    }
                }
            }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(updateMutation, updateVariables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result: {json}");
        var answer = JObject.Parse(json);

        var updatedEntities = answer.SelectToken("data.runtime.runtimeEntities.update") as JArray;
        updatedEntities.Should().NotBeNull();
        updatedEntities.Should().HaveCount(1);

        var attributes = updatedEntities![0]["attributes"]?["items"] as JArray;
        attributes.Should().NotBeNull();

        var mainSpec = attributes!.FirstOrDefault(a => a["attributeName"]?.Value<string>() == "mainSpecification");
        mainSpec.Should().NotBeNull();
        // The value should contain the updated specification
        var specValue = mainSpec!["value"]?.ToString();
        specValue.Should().Contain("Extended Pressure Range");
    }

    #endregion

    #region RecordArray Attribute Tests

    [Fact]
    public async Task Create_WithRecordArrayAttribute_ReturnsCreatedEntity()
    {
        // Arrange - Create entity with a RecordArray attribute (TechnicalSpecifications)
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

        var variables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = ProductCkTypeId,
                    rtWellKnownName = "TestProduct_RecordArray",
                    attributes = new object[]
                    {
                        new { attributeName = "productName", value = "Multi-Function Device MFD-3000" },
                        new { attributeName = "productCode", value = "MFD-3000" },
                        new
                        {
                            attributeName = "technicalSpecifications",
                            value = new[]
                            {
                                new { name = "Voltage", value = "24", unit = "V DC" },
                                new { name = "Current", value = "4-20", unit = "mA" },
                                new { name = "Temperature Range", value = "-20 to +60", unit = "°C" }
                            }
                        }
                    }
                }
            }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(mutation, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result: {json}");
        var answer = JObject.Parse(json);

        var createdEntities = answer.SelectToken("data.runtime.runtimeEntities.create") as JArray;
        createdEntities.Should().NotBeNull();
        createdEntities.Should().HaveCount(1);

        var attributes = createdEntities![0]["attributes"]?["items"] as JArray;
        attributes.Should().NotBeNull();

        // Verify the RecordArray attribute is present
        var techSpecs = attributes!.FirstOrDefault(a => a["attributeName"]?.Value<string>() == "technicalSpecifications");
        techSpecs.Should().NotBeNull("technicalSpecifications attribute should be present");
        techSpecs!["value"].Should().NotBeNull();
    }

    [Fact]
    public async Task Create_WithEmptyRecordArray_ReturnsCreatedEntity()
    {
        // Arrange - Create entity with an empty RecordArray
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

        var variables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = ProductCkTypeId,
                    rtWellKnownName = "TestProduct_EmptyRecordArray",
                    attributes = new object[]
                    {
                        new { attributeName = "productName", value = "Simple Product SP-100" },
                        new { attributeName = "productCode", value = "SP-100" },
                        new
                        {
                            attributeName = "technicalSpecifications",
                            value = Array.Empty<object>()
                        }
                    }
                }
            }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(mutation, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result: {json}");
        var answer = JObject.Parse(json);

        var createdEntities = answer.SelectToken("data.runtime.runtimeEntities.create") as JArray;
        createdEntities.Should().NotBeNull();
        createdEntities.Should().HaveCount(1);
    }

    [Fact]
    public async Task Update_RecordArrayAttribute_ReturnsUpdatedEntity()
    {
        // Arrange - First create an entity with a RecordArray
        var createMutation = @"
            mutation ($entities: [RtEntityInput!]!) {
                runtime {
                    runtimeEntities {
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
                    ckTypeId = ProductCkTypeId,
                    rtWellKnownName = "TestProduct_UpdateRecordArray",
                    attributes = new object[]
                    {
                        new { attributeName = "productName", value = "Sensor Array SA-500" },
                        new { attributeName = "productCode", value = "SA-500" },
                        new
                        {
                            attributeName = "technicalSpecifications",
                            value = new[]
                            {
                                new { name = "Channels", value = "8", unit = "" }
                            }
                        }
                    }
                }
            }
        });

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation, createVariables);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);
        var createdRtId = createAnswer.SelectToken("data.runtime.runtimeEntities.create[0].rtId")?.Value<string>();
        createdRtId.Should().NotBeNullOrEmpty();

        // Now update with additional specifications
        var updateMutation = @"
            mutation ($entities: [RtEntityUpdate!]!) {
                runtime {
                    runtimeEntities {
                        update(entities: $entities) {
                            rtId
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

        var updateVariables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    rtId = createdRtId,
                    item = new
                    {
                        ckTypeId = ProductCkTypeId,
                        attributes = new object[]
                        {
                            new
                            {
                                attributeName = "technicalSpecifications",
                                value = new[]
                                {
                                    new { name = "Channels", value = "16", unit = "" },
                                    new { name = "Resolution", value = "16", unit = "bit" },
                                    new { name = "Sample Rate", value = "1000", unit = "Hz" }
                                }
                            }
                        }
                    }
                }
            }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(updateMutation, updateVariables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result: {json}");
        var answer = JObject.Parse(json);

        var updatedEntities = answer.SelectToken("data.runtime.runtimeEntities.update") as JArray;
        updatedEntities.Should().NotBeNull();
        updatedEntities.Should().HaveCount(1);

        var attributes = updatedEntities![0]["attributes"]?["items"] as JArray;
        attributes.Should().NotBeNull();

        var techSpecs = attributes!.FirstOrDefault(a => a["attributeName"]?.Value<string>() == "technicalSpecifications");
        techSpecs.Should().NotBeNull();
        // The value should now contain the updated specifications
        var specsValue = techSpecs!["value"]?.ToString();
        specsValue.Should().Contain("Resolution");
        specsValue.Should().Contain("Sample Rate");
    }

    [Fact]
    public async Task Create_WithMultipleRecordArrays_ReturnsCreatedEntity()
    {
        // Arrange - Create entity with multiple RecordArray attributes
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

        var variables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = ProductCkTypeId,
                    rtWellKnownName = "TestProduct_MultipleRecordArrays",
                    attributes = new object[]
                    {
                        new { attributeName = "productName", value = "Complete System CS-1000" },
                        new { attributeName = "productCode", value = "CS-1000" },
                        new
                        {
                            attributeName = "technicalSpecifications",
                            value = new[]
                            {
                                new { name = "Power", value = "500", unit = "W" },
                                new { name = "Weight", value = "25", unit = "kg" }
                            }
                        },
                        new
                        {
                            attributeName = "contacts",
                            value = new[]
                            {
                                new { name = "Sales Team", email = "sales@example.com", phone = "+1-555-0101" },
                                new { name = "Technical Support", email = "support@example.com", phone = "+1-555-0102" }
                            }
                        }
                    }
                }
            }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(mutation, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result: {json}");
        var answer = JObject.Parse(json);

        var createdEntities = answer.SelectToken("data.runtime.runtimeEntities.create") as JArray;
        createdEntities.Should().NotBeNull();
        createdEntities.Should().HaveCount(1);

        var attributes = createdEntities![0]["attributes"]?["items"] as JArray;
        attributes.Should().NotBeNull();

        // Verify both RecordArray attributes are present
        var techSpecs = attributes!.FirstOrDefault(a => a["attributeName"]?.Value<string>() == "technicalSpecifications");
        techSpecs.Should().NotBeNull("technicalSpecifications should be present");

        var contacts = attributes.FirstOrDefault(a => a["attributeName"]?.Value<string>() == "contacts");
        contacts.Should().NotBeNull("contacts should be present");
    }

    #endregion

    #region BinaryLinked Attribute Tests

    [Fact]
    public async Task Create_WithBinaryLinkedAttribute_ReturnsCreatedEntity()
    {
        // Arrange - Create entity with BinaryLinked attribute set to null initially
        // Note: BinaryLinked attributes typically require binary upload which is done separately
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

        var variables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = ProductCkTypeId,
                    rtWellKnownName = "TestProduct_BinaryLinked",
                    attributes = new object[]
                    {
                        new { attributeName = "productName", value = "Document Test Product" },
                        new { attributeName = "productCode", value = "DTP-001" }
                        // BinaryLinked attributes are typically set via separate binary upload API
                    }
                }
            }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(mutation, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result: {json}");
        var answer = JObject.Parse(json);

        var createdEntities = answer.SelectToken("data.runtime.runtimeEntities.create") as JArray;
        createdEntities.Should().NotBeNull();
        createdEntities.Should().HaveCount(1);

        var createdEntity = createdEntities![0];
        createdEntity["rtId"].Should().NotBeNull();
        createdEntity["ckTypeId"]?.Value<string>().Should().Contain("Product");
    }

    #endregion

    #region Combined Complex Attribute Tests

    [Fact]
    public async Task Create_WithAllComplexAttributeTypes_ReturnsCreatedEntity()
    {
        // Arrange - Create entity with Record, RecordArray attributes
        var mutation = @"
            mutation ($entities: [RtEntityInput!]!) {
                runtime {
                    runtimeEntities {
                        create(entities: $entities) {
                            rtId
                            ckTypeId
                            attributes(first: 30) {
                                items {
                                    attributeName
                                    value
                                }
                            }
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
                    ckTypeId = ProductCkTypeId,
                    rtWellKnownName = "TestProduct_AllComplexTypes",
                    attributes = new object[]
                    {
                        new { attributeName = "productName", value = "Ultimate Industrial Controller UIC-9000" },
                        new { attributeName = "productCode", value = "UIC-9000" },
                        new { attributeName = "productDescription", value = "Enterprise-grade industrial controller with advanced features" },
                        // Record attributes
                        new
                        {
                            attributeName = "mainSpecification",
                            value = new
                            {
                                name = "Processing Power",
                                value = "1.2 GHz Dual Core",
                                unit = ""
                            }
                        },
                        new
                        {
                            attributeName = "manufacturerContact",
                            value = new
                            {
                                name = "Industrial Corp HQ",
                                email = "info@industrialcorp.com",
                                phone = "+1-800-555-0000"
                            }
                        },
                        // RecordArray attributes
                        new
                        {
                            attributeName = "technicalSpecifications",
                            value = new[]
                            {
                                new { name = "CPU", value = "ARM Cortex-A53", unit = "" },
                                new { name = "RAM", value = "4", unit = "GB" },
                                new { name = "Storage", value = "32", unit = "GB eMMC" },
                                new { name = "Operating Temperature", value = "-40 to +85", unit = "°C" },
                                new { name = "Humidity", value = "5 to 95", unit = "% RH" }
                            }
                        },
                        new
                        {
                            attributeName = "contacts",
                            value = new[]
                            {
                                new { name = "Sales Department", email = "sales@industrialcorp.com", phone = "+1-800-555-0001" },
                                new { name = "Technical Support", email = "support@industrialcorp.com", phone = "+1-800-555-0002" },
                                new { name = "Warranty Claims", email = "warranty@industrialcorp.com", phone = "+1-800-555-0003" }
                            }
                        }
                    }
                }
            }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(mutation, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result: {json}");
        var answer = JObject.Parse(json);

        var createdEntities = answer.SelectToken("data.runtime.runtimeEntities.create") as JArray;
        createdEntities.Should().NotBeNull();
        createdEntities.Should().HaveCount(1);

        var attributes = createdEntities![0]["attributes"]?["items"] as JArray;
        attributes.Should().NotBeNull();

        // Verify all complex attributes are present
        var attributeNames = attributes!.Select(a => a["attributeName"]?.Value<string>()).ToList();
        attributeNames.Should().Contain("productName");
        attributeNames.Should().Contain("productCode");
        attributeNames.Should().Contain("productDescription");
        attributeNames.Should().Contain("mainSpecification");
        attributeNames.Should().Contain("manufacturerContact");
        attributeNames.Should().Contain("technicalSpecifications");
        attributeNames.Should().Contain("contacts");
    }

    [Fact]
    public async Task Update_MultipleComplexAttributes_ReturnsUpdatedEntity()
    {
        // Arrange - First create an entity with complex attributes
        var createMutation = @"
            mutation ($entities: [RtEntityInput!]!) {
                runtime {
                    runtimeEntities {
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
                    ckTypeId = ProductCkTypeId,
                    rtWellKnownName = "TestProduct_UpdateComplex",
                    attributes = new object[]
                    {
                        new { attributeName = "productName", value = "Test Controller TC-100" },
                        new { attributeName = "productCode", value = "TC-100" },
                        new
                        {
                            attributeName = "mainSpecification",
                            value = new { name = "Version", value = "1.0", unit = "" }
                        },
                        new
                        {
                            attributeName = "technicalSpecifications",
                            value = new[]
                            {
                                new { name = "Initial Spec", value = "1", unit = "" }
                            }
                        }
                    }
                }
            }
        });

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation, createVariables);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);
        var createdRtId = createAnswer.SelectToken("data.runtime.runtimeEntities.create[0].rtId")?.Value<string>();
        createdRtId.Should().NotBeNullOrEmpty();

        // Update multiple complex attributes at once
        var updateMutation = @"
            mutation ($entities: [RtEntityUpdate!]!) {
                runtime {
                    runtimeEntities {
                        update(entities: $entities) {
                            rtId
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

        var updateVariables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    rtId = createdRtId,
                    item = new
                    {
                        ckTypeId = ProductCkTypeId,
                        attributes = new object[]
                        {
                            new { attributeName = "productName", value = "Updated Controller TC-200" },
                            new
                            {
                                attributeName = "mainSpecification",
                                value = new { name = "Version", value = "2.0", unit = "" }
                            },
                            new
                            {
                                attributeName = "technicalSpecifications",
                                value = new[]
                                {
                                    new { name = "Updated Spec 1", value = "A", unit = "" },
                                    new { name = "Updated Spec 2", value = "B", unit = "" }
                                }
                            },
                            new
                            {
                                attributeName = "manufacturerContact",
                                value = new
                                {
                                    name = "New Contact",
                                    email = "new@example.com",
                                    phone = "+1-555-9999"
                                }
                            }
                        }
                    }
                }
            }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(updateMutation, updateVariables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result: {json}");
        var answer = JObject.Parse(json);

        var updatedEntities = answer.SelectToken("data.runtime.runtimeEntities.update") as JArray;
        updatedEntities.Should().NotBeNull();
        updatedEntities.Should().HaveCount(1);

        var attributes = updatedEntities![0]["attributes"]?["items"] as JArray;
        attributes.Should().NotBeNull();

        // Verify updates
        var productName = attributes!.FirstOrDefault(a => a["attributeName"]?.Value<string>() == "productName");
        productName?["value"]?.Value<string>().Should().Be("Updated Controller TC-200");

        var mainSpec = attributes.FirstOrDefault(a => a["attributeName"]?.Value<string>() == "mainSpecification");
        mainSpec.Should().NotBeNull();
        mainSpec!["value"]?.ToString().Should().Contain("2.0");

        var manufacturerContact = attributes.FirstOrDefault(a => a["attributeName"]?.Value<string>() == "manufacturerContact");
        manufacturerContact.Should().NotBeNull("manufacturerContact should be present after update");
    }

    [Fact]
    public async Task Delete_EntityWithComplexAttributes_ReturnsTrue()
    {
        // Arrange - Create an entity with complex attributes
        var createMutation = @"
            mutation ($entities: [RtEntityInput!]!) {
                runtime {
                    runtimeEntities {
                        create(entities: $entities) {
                            rtId
                            ckTypeId
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
                    ckTypeId = ProductCkTypeId,
                    rtWellKnownName = "TestProduct_DeleteComplex",
                    attributes = new object[]
                    {
                        new { attributeName = "productName", value = "Delete Test Product" },
                        new { attributeName = "productCode", value = "DEL-001" },
                        new
                        {
                            attributeName = "mainSpecification",
                            value = new { name = "ToBeDeleted", value = "Yes", unit = "" }
                        },
                        new
                        {
                            attributeName = "technicalSpecifications",
                            value = new[]
                            {
                                new { name = "Spec1", value = "V1", unit = "" },
                                new { name = "Spec2", value = "V2", unit = "" }
                            }
                        }
                    }
                }
            }
        });

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation, createVariables);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);
        var rtId = createAnswer.SelectToken("data.runtime.runtimeEntities.create[0].rtId")?.Value<string>();
        var ckTypeId = createAnswer.SelectToken("data.runtime.runtimeEntities.create[0].ckTypeId")?.Value<string>();

        // Delete the entity
        var deleteMutation = @"
            mutation ($entities: [RtEntityId!]!) {
                runtime {
                    runtimeEntities {
                        delete(entities: $entities)
                    }
                }
            }";

        var deleteVariables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new { rtId, ckTypeId }
            }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(deleteMutation, deleteVariables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result: {json}");
        var answer = JObject.Parse(json);

        var deleteResult = answer.SelectToken("data.runtime.runtimeEntities.delete")?.Value<bool>();
        deleteResult.Should().BeTrue();
    }

    #endregion

    #region Record Attribute with Partial Fields Tests

    [Fact]
    public async Task Create_WithRecordAttributePartialFields_ReturnsCreatedEntity()
    {
        // Arrange - Create entity with Record attribute having only required fields
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

        var variables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = ProductCkTypeId,
                    rtWellKnownName = "TestProduct_RecordPartialFields",
                    attributes = new object[]
                    {
                        new { attributeName = "productName", value = "Minimal Spec Product" },
                        new { attributeName = "productCode", value = "MSP-001" },
                        new
                        {
                            attributeName = "mainSpecification",
                            value = new
                            {
                                name = "Basic Spec",
                                value = "Minimal"
                                // unit is optional, not provided
                            }
                        },
                        new
                        {
                            attributeName = "manufacturerContact",
                            value = new
                            {
                                name = "Contact Only"
                                // email and phone are optional, not provided
                            }
                        }
                    }
                }
            }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(mutation, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result: {json}");
        var answer = JObject.Parse(json);

        var createdEntities = answer.SelectToken("data.runtime.runtimeEntities.create") as JArray;
        createdEntities.Should().NotBeNull();
        createdEntities.Should().HaveCount(1);
    }

    #endregion
}
