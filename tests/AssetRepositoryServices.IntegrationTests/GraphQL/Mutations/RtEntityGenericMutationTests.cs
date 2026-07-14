using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using MongoDB.Bson;
using Newtonsoft.Json.Linq;
using System.Text.Json;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Mutations;

/// <summary>
/// Integration tests for the generic Runtime Entity mutations (create, update, delete).
/// Tests the GraphQL schema directly without HTTP.
/// Uses the AssetRepositoryIntegrationTest model.
/// </summary>
[Collection("Sequential")]
public class RtEntityGenericMutationTests : IClassFixture<GraphQlTestFixture>
{
    private readonly GraphQlTestFixture _fixture;

    // CkTypeId for testing (rtCkId format)
    private const string CustomerCkTypeId = "AssetRepositoryIntegrationTest/Customer";
    private const string MeteringPointCkTypeId = "AssetRepositoryIntegrationTest/MeteringPoint";

    public RtEntityGenericMutationTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    #region Create Mutation Tests

    [Fact]
    public async Task Create_SingleEntity_ReturnsCreatedEntity()
    {
        // Arrange
        var mutation = @"
            mutation ($entities: [RtEntityInput!]!) {
                runtime {
                    runtimeEntities {
                        create(entities: $entities) {
                            rtId
                            ckTypeId
                            attributes(first: 10) {
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
                    ckTypeId = CustomerCkTypeId,
                    rtWellKnownName = "TestCustomer_Create_Single",
                    attributes = new[]
                    {
                        new { attributeName = "firstName", value = "John" },
                        new { attributeName = "lastName", value = "Doe" },
                        new { attributeName = "street", value = "Test Street 1" },
                        new { attributeName = "postalCode", value = "12345" },
                        new { attributeName = "city", value = "Test City" },
                        new { attributeName = "country", value = "Austria" }
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
        var answer = JObject.Parse(json);

        var createdEntities = answer.SelectToken("data.runtime.runtimeEntities.create") as JArray;
        createdEntities.Should().NotBeNull();
        createdEntities.Should().HaveCount(1);

        var createdEntity = createdEntities[0];
        createdEntity["rtId"].Should().NotBeNull();
        createdEntity["ckTypeId"]?.Value<string>().Should().Contain("Customer");
    }

    [Fact]
    public async Task Create_MultipleEntities_ReturnsAllCreatedEntities()
    {
        // Arrange
        var mutation = @"
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

        var variables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = CustomerCkTypeId,
                    rtWellKnownName = "TestCustomer_Create_Multi_1",
                    attributes = new[]
                    {
                        new { attributeName = "firstName", value = "Alice" },
                        new { attributeName = "lastName", value = "Smith" },
                        new { attributeName = "street", value = "Test Street 2" },
                        new { attributeName = "postalCode", value = "12345" },
                        new { attributeName = "city", value = "Test City" },
                        new { attributeName = "country", value = "Austria" }
                    }
                },
                new
                {
                    ckTypeId = CustomerCkTypeId,
                    rtWellKnownName = "TestCustomer_Create_Multi_2",
                    attributes = new[]
                    {
                        new { attributeName = "firstName", value = "Bob" },
                        new { attributeName = "lastName", value = "Johnson" },
                        new { attributeName = "street", value = "Test Street 3" },
                        new { attributeName = "postalCode", value = "12346" },
                        new { attributeName = "city", value = "Test City" },
                        new { attributeName = "country", value = "Austria" }
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
        var answer = JObject.Parse(json);

        var createdEntities = answer.SelectToken("data.runtime.runtimeEntities.create") as JArray;
        createdEntities.Should().NotBeNull();
        createdEntities.Should().HaveCount(2);
    }

    [Fact]
    public async Task Create_EmptyEntitiesList_ReturnsEmptyResult()
    {
        // Arrange
        var mutation = @"
            mutation ($entities: [RtEntityInput!]!) {
                runtime {
                    runtimeEntities {
                        create(entities: $entities) {
                            rtId
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new
        {
            entities = Array.Empty<object>()
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(mutation, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var createdEntities = answer.SelectToken("data.runtime.runtimeEntities.create") as JArray;
        createdEntities.Should().NotBeNull();
        createdEntities.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_DifferentCkTypeIds_ReturnsError()
    {
        // Arrange - entities with different CkTypeIds should fail
        var mutation = @"
            mutation ($entities: [RtEntityInput!]!) {
                runtime {
                    runtimeEntities {
                        create(entities: $entities) {
                            rtId
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
                    ckTypeId = CustomerCkTypeId,
                    rtWellKnownName = "TestCustomer_DifferentTypes",
                    attributes = new[]
                    {
                        new { attributeName = "firstName", value = "Test" },
                        new { attributeName = "lastName", value = "User" },
                        new { attributeName = "street", value = "Street" },
                        new { attributeName = "postalCode", value = "12345" },
                        new { attributeName = "city", value = "City" },
                        new { attributeName = "country", value = "Austria" }
                    }
                },
                new
                {
                    ckTypeId = MeteringPointCkTypeId,
                    rtWellKnownName = "TestMeteringPoint_DifferentTypes",
                    attributes = new[]
                    {
                        new { attributeName = "meteringPointNumber", value = "AT001234567890" }
                    }
                }
            }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(mutation, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().NotBeNullOrEmpty("Creating entities with different CkTypeIds should produce an error");
        result.Errors!.First().Message.Should().Contain("CkTypeId");
    }

    [Fact]
    public async Task Create_WithOptionalAttributes_ReturnsCreatedEntity()
    {
        // Arrange - create entity with optional attributes
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
                    ckTypeId = CustomerCkTypeId,
                    rtWellKnownName = "TestCustomer_OptionalAttrs",
                    attributes = new[]
                    {
                        new { attributeName = "firstName", value = "Jane" },
                        new { attributeName = "lastName", value = "Doe" },
                        new { attributeName = "street", value = "Test Street 4" },
                        new { attributeName = "postalCode", value = "12347" },
                        new { attributeName = "city", value = "Test City" },
                        new { attributeName = "country", value = "Austria" },
                        new { attributeName = "eMailAddress", value = "jane.doe@example.com" },
                        new { attributeName = "phoneNumberMobile", value = "+43 664 1234567" },
                        new { attributeName = "companyName", value = "Test Company GmbH" }
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
        var answer = JObject.Parse(json);

        var createdEntities = answer.SelectToken("data.runtime.runtimeEntities.create") as JArray;
        createdEntities.Should().NotBeNull();
        createdEntities.Should().HaveCount(1);

        var createdEntity = createdEntities![0];
        var attributes = createdEntity["attributes"]?["items"] as JArray;
        attributes.Should().NotBeNull();

        // Verify optional attributes are present
        var attributeNames = attributes!.Select(a => a["attributeName"]?.Value<string>()).ToList();
        attributeNames.Should().Contain("eMailAddress");
        attributeNames.Should().Contain("phoneNumberMobile");
        attributeNames.Should().Contain("companyName");
    }

    #endregion

    #region Update Mutation Tests

    [Fact]
    public async Task Update_SingleEntity_ReturnsUpdatedEntity()
    {
        // Arrange - first create an entity to update
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
                    ckTypeId = CustomerCkTypeId,
                    rtWellKnownName = "TestCustomer_Update_Single",
                    attributes = new[]
                    {
                        new { attributeName = "firstName", value = "Original" },
                        new { attributeName = "lastName", value = "Name" },
                        new { attributeName = "street", value = "Original Street" },
                        new { attributeName = "postalCode", value = "11111" },
                        new { attributeName = "city", value = "Original City" },
                        new { attributeName = "country", value = "Austria" }
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

        // Now update the entity
        var updateMutation = @"
            mutation ($entities: [RtEntityUpdate!]!) {
                runtime {
                    runtimeEntities {
                        update(entities: $entities) {
                            rtId
                            attributes(first: 10) {
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
                        ckTypeId = CustomerCkTypeId,
                        attributes = new[]
                        {
                            new { attributeName = "firstName", value = "Updated" },
                            new { attributeName = "lastName", value = "Customer" }
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
        var answer = JObject.Parse(json);

        var updatedEntities = answer.SelectToken("data.runtime.runtimeEntities.update") as JArray;
        updatedEntities.Should().NotBeNull();
        updatedEntities.Should().HaveCount(1);

        var updatedEntity = updatedEntities![0];
        updatedEntity["rtId"]?.Value<string>().Should().Be(createdRtId);

        var attributes = updatedEntity["attributes"]?["items"] as JArray;
        attributes.Should().NotBeNull();

        var firstName = attributes!.FirstOrDefault(a => a["attributeName"]?.Value<string>() == "firstName");
        firstName?["value"]?.Value<string>().Should().Be("Updated");

        var lastName = attributes.FirstOrDefault(a => a["attributeName"]?.Value<string>() == "lastName");
        lastName?["value"]?.Value<string>().Should().Be("Customer");
    }

    [Fact]
    public async Task Update_RtWellKnownNameOnly_ReturnsRenamedEntity()
    {
        // Regression: without any `attributes` payload, the resolver previously skipped the
        // update entirely and returned an empty array — surfacing as
        // "Server did not return an updated archive" in the studio's archive rename flow.
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
                    ckTypeId = CustomerCkTypeId,
                    rtWellKnownName = "TestCustomer_RenameOnly_Original",
                    attributes = new[]
                    {
                        new { attributeName = "firstName", value = "Rename" },
                        new { attributeName = "lastName", value = "Only" },
                        new { attributeName = "street", value = "Street 1" },
                        new { attributeName = "postalCode", value = "12345" },
                        new { attributeName = "city", value = "City" },
                        new { attributeName = "country", value = "Austria" }
                    }
                }
            }
        });

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation, createVariables);
        createResult.Errors.Should().BeNullOrEmpty();

        var createdRtId = JObject.Parse(_fixture.SerializeGraphQl(createResult))
            .SelectToken("data.runtime.runtimeEntities.create[0].rtId")?.Value<string>();
        createdRtId.Should().NotBeNullOrEmpty();

        var updateMutation = @"
            mutation ($entities: [RtEntityUpdate!]!) {
                runtime {
                    runtimeEntities {
                        update(entities: $entities) {
                            rtId
                            rtWellKnownName
                        }
                    }
                }
            }";

        // The generic input schema marks `attributes` as non-null, so we send an empty list
        // (no attribute changes) — same shape the bug originally reproduced under, since the
        // pre-fix guard `if (document.Attributes.Any())` skipped the update in both the
        // omitted-attributes and empty-attributes cases.
        var updateVariables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    rtId = createdRtId,
                    item = new
                    {
                        ckTypeId = CustomerCkTypeId,
                        rtWellKnownName = "TestCustomer_RenameOnly_Renamed",
                        attributes = Array.Empty<object>()
                    }
                }
            }
        });

        var result = await _fixture.ExecuteGraphQlAsync(updateMutation, updateVariables);

        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var updatedEntities = JObject.Parse(_fixture.SerializeGraphQl(result))
            .SelectToken("data.runtime.runtimeEntities.update") as JArray;
        updatedEntities.Should().NotBeNull();
        updatedEntities.Should().HaveCount(1);
        updatedEntities![0]["rtId"]?.Value<string>().Should().Be(createdRtId);
        updatedEntities![0]["rtWellKnownName"]?.Value<string>().Should().Be("TestCustomer_RenameOnly_Renamed");
    }

    [Fact]
    public async Task Update_MultipleEntities_ReturnsAllUpdatedEntities()
    {
        // Arrange - create two entities to update
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
                    ckTypeId = CustomerCkTypeId,
                    rtWellKnownName = "TestCustomer_Update_Multi_1",
                    attributes = new[]
                    {
                        new { attributeName = "firstName", value = "First1" },
                        new { attributeName = "lastName", value = "Last1" },
                        new { attributeName = "street", value = "Street 1" },
                        new { attributeName = "postalCode", value = "11111" },
                        new { attributeName = "city", value = "City1" },
                        new { attributeName = "country", value = "Austria" }
                    }
                },
                new
                {
                    ckTypeId = CustomerCkTypeId,
                    rtWellKnownName = "TestCustomer_Update_Multi_2",
                    attributes = new[]
                    {
                        new { attributeName = "firstName", value = "First2" },
                        new { attributeName = "lastName", value = "Last2" },
                        new { attributeName = "street", value = "Street 2" },
                        new { attributeName = "postalCode", value = "22222" },
                        new { attributeName = "city", value = "City2" },
                        new { attributeName = "country", value = "Austria" }
                    }
                }
            }
        });

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation, createVariables);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);
        var createdEntities = createAnswer.SelectToken("data.runtime.runtimeEntities.create") as JArray;
        createdEntities.Should().HaveCount(2);

        var rtId1 = createdEntities![0]["rtId"]?.Value<string>();
        var rtId2 = createdEntities[1]["rtId"]?.Value<string>();

        // Now update both entities
        var updateMutation = @"
            mutation ($entities: [RtEntityUpdate!]!) {
                runtime {
                    runtimeEntities {
                        update(entities: $entities) {
                            rtId
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
                    rtId = rtId1,
                    item = new
                    {
                        ckTypeId = CustomerCkTypeId,
                        attributes = new[]
                        {
                            new { attributeName = "firstName", value = "Updated1" }
                        }
                    }
                },
                new
                {
                    rtId = rtId2,
                    item = new
                    {
                        ckTypeId = CustomerCkTypeId,
                        attributes = new[]
                        {
                            new { attributeName = "firstName", value = "Updated2" }
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
        var answer = JObject.Parse(json);

        var updatedEntities = answer.SelectToken("data.runtime.runtimeEntities.update") as JArray;
        updatedEntities.Should().NotBeNull();
        updatedEntities.Should().HaveCount(2);
    }

    [Fact]
    public async Task Update_EmptyEntitiesList_ReturnsEmptyResult()
    {
        // Arrange
        var mutation = @"
            mutation ($entities: [RtEntityUpdate!]!) {
                runtime {
                    runtimeEntities {
                        update(entities: $entities) {
                            rtId
                        }
                    }
                }
            }";

        var variables = JsonSerializer.Serialize(new
        {
            entities = Array.Empty<object>()
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(mutation, variables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var updatedEntities = answer.SelectToken("data.runtime.runtimeEntities.update") as JArray;
        updatedEntities.Should().NotBeNull();
        updatedEntities.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_DifferentCkTypeIds_ReturnsError()
    {
        // Arrange - create entities for the test
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
                    ckTypeId = CustomerCkTypeId,
                    rtWellKnownName = "TestCustomer_Update_DifferentTypes",
                    attributes = new[]
                    {
                        new { attributeName = "firstName", value = "Test" },
                        new { attributeName = "lastName", value = "User" },
                        new { attributeName = "street", value = "Street" },
                        new { attributeName = "postalCode", value = "12345" },
                        new { attributeName = "city", value = "City" },
                        new { attributeName = "country", value = "Austria" }
                    }
                }
            }
        });

        var createResult = await _fixture.ExecuteGraphQlAsync(createMutation, createVariables);
        createResult.Errors.Should().BeNullOrEmpty();

        var createJson = _fixture.SerializeGraphQl(createResult);
        var createAnswer = JObject.Parse(createJson);
        var customerRtId = createAnswer.SelectToken("data.runtime.runtimeEntities.create[0].rtId")?.Value<string>();

        // Create a metering point - must include all mandatory attributes
        var createMeteringPointVariables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = MeteringPointCkTypeId,
                    rtWellKnownName = "TestMeteringPoint_Update_DifferentTypes",
                    attributes = new object[]
                    {
                        new { attributeName = "meteringPointNumber", value = "AT001234567890" },
                        new { attributeName = "meterReading", value = 100.0 },
                        new { attributeName = "operatingStatus", value = 0 },
                        new { attributeName = "name", value = "Test MeteringPoint" },
                        new { attributeName = "description", value = "Test description" }
                    }
                }
            }
        });

        var createMpResult = await _fixture.ExecuteGraphQlAsync(createMutation, createMeteringPointVariables);
        createMpResult.Errors.Should().BeNullOrEmpty();

        var createMpJson = _fixture.SerializeGraphQl(createMpResult);
        var createMpAnswer = JObject.Parse(createMpJson);
        var mpRtId = createMpAnswer.SelectToken("data.runtime.runtimeEntities.create[0].rtId")?.Value<string>();

        // Try to update both with different CkTypeIds
        var updateMutation = @"
            mutation ($entities: [RtEntityUpdate!]!) {
                runtime {
                    runtimeEntities {
                        update(entities: $entities) {
                            rtId
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
                    rtId = customerRtId,
                    item = new
                    {
                        ckTypeId = CustomerCkTypeId,
                        attributes = new[]
                        {
                            new { attributeName = "firstName", value = "Updated" }
                        }
                    }
                },
                new
                {
                    rtId = mpRtId,
                    item = new
                    {
                        ckTypeId = MeteringPointCkTypeId,
                        attributes = new[]
                        {
                            new { attributeName = "meteringPointNumber", value = "AT00999999999" }
                        }
                    }
                }
            }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(updateMutation, updateVariables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().NotBeNullOrEmpty("Updating entities with different CkTypeIds should produce an error");
        result.Errors!.First().Message.Should().Contain("CkTypeId");
    }

    #endregion

    #region Delete Mutation Tests

    [Fact]
    public async Task Delete_SingleEntity_ReturnsTrue()
    {
        // Arrange - create an entity to delete
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
                    ckTypeId = CustomerCkTypeId,
                    rtWellKnownName = "TestCustomer_Delete_Single",
                    attributes = new[]
                    {
                        new { attributeName = "firstName", value = "ToDelete" },
                        new { attributeName = "lastName", value = "User" },
                        new { attributeName = "street", value = "Street" },
                        new { attributeName = "postalCode", value = "12345" },
                        new { attributeName = "city", value = "City" },
                        new { attributeName = "country", value = "Austria" }
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

        // Now delete the entity
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
        var answer = JObject.Parse(json);

        var deleteResult = answer.SelectToken("data.runtime.runtimeEntities.delete")?.Value<bool>();
        deleteResult.Should().BeTrue();
    }

    #endregion

    #region Enum Coercion Tests (AB#4391)

    // MeteringPoint inherits from Asset, so it is stored in RtEntity_AssetRepositoryIntegrationTestAsset.
    // OperatingStatus is a (mandatory) Enum attribute: Unknown=0, OK=1, Maintenance=2.
    private const string AssetCollectionSuffix = "AssetRepositoryIntegrationTestAsset";

    private static string CreateMeteringPointVariables(string rtWellKnownName, object operatingStatusValue)
    {
        return JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = MeteringPointCkTypeId,
                    rtWellKnownName,
                    attributes = new object[]
                    {
                        new { attributeName = "meteringPointNumber", value = "AT001234567890" },
                        new { attributeName = "meterReading", value = 100 },
                        new { attributeName = "operatingStatus", value = operatingStatusValue },
                        new { attributeName = "name", value = "Test MeteringPoint" },
                        new { attributeName = "description", value = "Test description" }
                    }
                }
            }
        });
    }

    private const string CreateMeteringPointMutation = @"
        mutation ($entities: [RtEntityInput!]!) {
            runtime {
                runtimeEntities {
                    create(entities: $entities) {
                        rtId
                    }
                }
            }
        }";

    [Fact]
    public async Task Create_EnumAttributeByName_StoresIntegerKey()
    {
        // Arrange - write the enum by its NAME ("Maintenance"), the shape a generic-mutation client
        // (e.g. the meshmakers-app frontend) would send. Pre-fix this was stored verbatim as the
        // string "Maintenance" instead of the integer key 2, silently corrupting the enum field.
        var variables = CreateMeteringPointVariables("MeteringPoint_Enum_ByName", "Maintenance");

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(CreateMeteringPointMutation, variables);

        // Assert
        result.Errors.Should().BeNullOrEmpty();
        var rtId = JObject.Parse(_fixture.SerializeGraphQl(result))
            .SelectToken("data.runtime.runtimeEntities.create[0].rtId")?.Value<string>();
        rtId.Should().NotBeNullOrEmpty();

        var raw = await _fixture.ReadRawAttributeValueFromMongoDb(rtId!, "operatingStatus", AssetCollectionSuffix);
        raw.BsonType.Should().Be(BsonType.Int32, "the enum name must be coerced to its integer key, not stored as a string");
        raw.AsInt32.Should().Be(2);
    }

    [Fact]
    public async Task Create_EnumAttributeByIntegerKey_StoresIntegerKey()
    {
        // Arrange - integer keys (the frontend mitigation) must keep working and round-trip unchanged.
        var variables = CreateMeteringPointVariables("MeteringPoint_Enum_ByKey", 1);

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(CreateMeteringPointMutation, variables);

        // Assert
        result.Errors.Should().BeNullOrEmpty();
        var rtId = JObject.Parse(_fixture.SerializeGraphQl(result))
            .SelectToken("data.runtime.runtimeEntities.create[0].rtId")?.Value<string>();
        rtId.Should().NotBeNullOrEmpty();

        var raw = await _fixture.ReadRawAttributeValueFromMongoDb(rtId!, "operatingStatus", AssetCollectionSuffix);
        raw.BsonType.Should().Be(BsonType.Int32);
        raw.AsInt32.Should().Be(1);
    }

    [Fact]
    public async Task Create_EnumAttributeInvalidName_ReturnsError()
    {
        // Arrange - an undefined enum value must be rejected, not silently stored.
        var variables = CreateMeteringPointVariables("MeteringPoint_Enum_Invalid", "NotARealStatus");

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(CreateMeteringPointMutation, variables);

        // Assert
        result.Errors.Should().NotBeNullOrEmpty("an invalid enum value must produce an error");
        result.Errors!.First().Message.Should().Contain("OperatingStatus");
    }

    #endregion

    #region Association Tests

    [Fact]
    public async Task Create_WithAssociation_CreatesEntityAndAssociation()
    {
        // Arrange - first create a customer to link to
        var createCustomerMutation = @"
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

        var customerVariables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = CustomerCkTypeId,
                    rtWellKnownName = "TestCustomer_Association",
                    attributes = new[]
                    {
                        new { attributeName = "firstName", value = "Association" },
                        new { attributeName = "lastName", value = "Test" },
                        new { attributeName = "street", value = "Test Street" },
                        new { attributeName = "postalCode", value = "12345" },
                        new { attributeName = "city", value = "Test City" },
                        new { attributeName = "country", value = "Austria" }
                    }
                }
            }
        });

        var customerResult = await _fixture.ExecuteGraphQlAsync(createCustomerMutation, customerVariables);
        customerResult.Errors.Should().BeNullOrEmpty();

        var customerJson = _fixture.SerializeGraphQl(customerResult);
        var customerAnswer = JObject.Parse(customerJson);
        var customerRtId = customerAnswer.SelectToken("data.runtime.runtimeEntities.create[0].rtId")?.Value<string>();
        var customerCkTypeId = customerAnswer.SelectToken("data.runtime.runtimeEntities.create[0].ckTypeId")?.Value<string>();
        customerRtId.Should().NotBeNullOrEmpty();

        // Now create an OperatingFacility with association to the customer
        var operatingFacilityCkTypeId = "AssetRepositoryIntegrationTest/OperatingFacility";
        var createFacilityMutation = @"
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

        var facilityVariables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = operatingFacilityCkTypeId,
                    rtWellKnownName = "TestFacility_WithAssociation",
                    attributes = new[]
                    {
                        new { attributeName = "name", value = "Test Facility" },
                        new { attributeName = "description", value = "Test Description" }
                    },
                    associations = new[]
                    {
                        new
                        {
                            roleName = "OwnedBy",
                            targets = new[]
                            {
                                new
                                {
                                    target = new { rtId = customerRtId, ckTypeId = customerCkTypeId },
                                    modOption = "CREATE"
                                }
                            }
                        }
                    }
                }
            }
        });

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(createFacilityMutation, facilityVariables);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        var answer = JObject.Parse(json);

        var createdEntities = answer.SelectToken("data.runtime.runtimeEntities.create") as JArray;
        createdEntities.Should().NotBeNull();
        createdEntities.Should().HaveCount(1);

        var createdEntity = createdEntities![0];
        var facilityRtId = createdEntity["rtId"]?.Value<string>();
        facilityRtId.Should().NotBeNullOrEmpty();

        // Verify the association was created by querying the customer to check its inverse association
        var queryMutation = $@"
            query {{
                runtime {{
                    assetRepositoryIntegrationTestCustomer(first: 100, after: null) {{
                        items {{
                            rtId
                            owns(first: 10, ckTypeIds: [""AssetRepositoryIntegrationTest/OperatingFacility""]) {{
                                items {{
                                    ... on AssetRepositoryIntegrationTestOperatingFacility {{
                                        rtId
                                    }}
                                }}
                            }}
                        }}
                    }}
                }}
            }}";

        var queryResult = await _fixture.ExecuteGraphQlAsync(queryMutation, "{}");
        queryResult.Errors.Should().BeNullOrEmpty();

        var queryJson = _fixture.SerializeGraphQl(queryResult);
        var queryAnswer = JObject.Parse(queryJson);

        // Find the customer we created
        var customers = queryAnswer.SelectToken("data.runtime.assetRepositoryIntegrationTestCustomer.items") as JArray;
        customers.Should().NotBeNull();

        var ourCustomer = customers!.FirstOrDefault(c => c["rtId"]?.Value<string>() == customerRtId);
        ourCustomer.Should().NotBeNull("The created customer should be found");

        var owns = ourCustomer!["owns"]?["items"] as JArray;
        owns.Should().NotBeNull();
        owns.Should().HaveCount(1);
        owns![0]["rtId"]?.Value<string>().Should().Be(facilityRtId);
    }

    #endregion
}
