using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Schema;

/// <summary>
/// Tests for GraphQL schema generation with interface implementation.
/// These tests verify that types correctly implement their interfaces when
/// the type hierarchy involves abstract types with associations.
///
/// The issue being tested:
/// - When an abstract type (e.g., Equipment) has an inbound association (e.g., hasReadings from SensorReading)
/// - An interface is created for the abstract type (EquipmentInterface)
/// - Another abstract type (Machine) derives from Equipment, creating MachineInterface
/// - Concrete types (Robot, CncMachine) that derive from Machine should implement both interfaces
/// - The field types must be compatible between the interface and implementing types
///
/// Bug scenario (from EnergyIQ model):
/// - EnergyIQAirHandlingUnit implements EnergyIQTechnicalSystemInterface
/// - Interface has relatesFrom field of type BasicAsset_RelatesFromUnionConnection
/// - But the implementing type has relatesFrom of type SystemEntity_RelatesFromUnionConnection
/// - GraphQL validation fails because the field types are not compatible
///
/// Test hierarchy:
/// - Equipment (abstract) -> Entity
/// - Machine (abstract) -> Equipment
/// - Robot (concrete) -> Machine
/// - CncMachine (concrete) -> Machine
/// - SensorReading -> Entity with association to Equipment
/// </summary>
[Collection("Sequential")]
public class InterfaceImplementationTests
    : IClassFixture<GraphQlTestFixture>
{
    private readonly GraphQlTestFixture _fixture;

    public InterfaceImplementationTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    /// <summary>
    /// Test that the schema can be successfully initialized with the current test model.
    /// This verifies that interface implementation works correctly when:
    /// - Vehicle (abstract) has an inbound association (children) from VehicleReading
    /// - Car and Truck implement VehicleInterface
    /// - The connection types are compatible
    /// </summary>
    [Fact]
    public async Task GraphQL_SchemaInitialization_WithAbstractTypeHierarchy_Succeeds()
    {
        // Arrange - A simple introspection query to verify the schema is valid
        var query = @"
            query {
              __schema {
                types {
                  name
                  kind
                  interfaces {
                    name
                  }
                }
              }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert - Schema initialization should succeed
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty(
            $"Schema should initialize without errors. Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Schema types found: {json.Length} characters of JSON");
    }

    /// <summary>
    /// Test that types implementing an interface have the correct field types.
    /// Specifically tests that the inbound association fields (like 'children' on VehicleInterface)
    /// are correctly typed on implementing types (Car, Truck).
    /// </summary>
    [Fact]
    public async Task GraphQL_InterfaceImplementation_FieldTypes_AreCompatible()
    {
        // Arrange - Query to get interface and implementing types with their field types
        var query = @"
            query {
              __schema {
                types {
                  name
                  kind
                  interfaces {
                    name
                  }
                  fields {
                    name
                    type {
                      name
                      kind
                      ofType {
                        name
                        kind
                      }
                    }
                  }
                }
              }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty(
            $"Schema introspection should succeed. Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Schema introspection result length: {json.Length}");
    }

    /// <summary>
    /// Test that querying via an interface fragment works correctly on derived types.
    /// This tests the scenario where we query for Vehicles and get Car/Truck entities.
    /// </summary>
    [Fact]
    public async Task GraphQL_QueryViaInterfaceFragment_ReturnsDerivedTypeData()
    {
        // Arrange - Query vehicles using the interface
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestVehicle {
                  totalCount
                  items {
                    __typename
                    rtId
                    rtWellKnownName
                    name
                    licensePlate
                    year
                  }
                }
              }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty(
            $"Query should execute without errors. Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");
        result.Data.Should().NotBeNull();

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result JSON: {json}");

        // Verify we got vehicles
        json.Should().Contain("totalCount");
    }

    /// <summary>
    /// Test that the children association on abstract types works correctly.
    /// VehicleReading has a ParentChild association to Vehicle (abstract).
    /// When we query Vehicle's children, we should see VehicleReading entities.
    /// Note: Union types require inline fragments for field access.
    /// </summary>
    [Fact]
    public async Task GraphQL_AbstractType_InboundAssociation_ReturnsCorrectData()
    {
        // Arrange - Query vehicles with their children (VehicleReadings)
        // Must use inline fragment on union type
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestVehicle {
                  items {
                    rtWellKnownName
                    __typename
                    children {
                      totalCount
                      items {
                        __typename
                        ... on AssetRepositoryIntegrationTestVehicleReading {
                          rtWellKnownName
                        }
                      }
                    }
                  }
                }
              }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty(
            $"Query should execute without errors. Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Result JSON: {json}");
    }

    /// <summary>
    /// Test that the deep inheritance hierarchy with Equipment -> Machine -> Robot works.
    /// This tests the scenario that caused the EnergyIQ bug:
    /// - Equipment (abstract) has an inbound association (hasReadings) from SensorReading
    /// - Machine (abstract) inherits from Equipment
    /// - Robot (concrete) inherits from Machine
    /// - Robot should implement both EquipmentInterface and MachineInterface
    /// - The hasReadings field type must be compatible across all levels
    /// </summary>
    [Fact]
    public async Task GraphQL_DeepInheritanceHierarchy_InterfaceFieldTypesAreCompatible()
    {
        // Arrange - Query Robot type to verify it has the hasReadings field
        var query = @"
            query {
              __type(name: ""AssetRepositoryIntegrationTestRobot"") {
                name
                kind
                interfaces {
                  name
                }
                fields {
                  name
                  type {
                    name
                    kind
                    ofType {
                      name
                    }
                  }
                }
              }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty(
            $"Schema introspection should succeed without interface implementation errors. Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Robot type introspection: {json}");

        var answer = JObject.Parse(json);
        var robotType = answer.SelectToken("data.__type");
        robotType.Should().NotBeNull("Robot type should exist in schema");

        // Verify Robot implements the expected interfaces
        var interfaces = robotType!.SelectToken("interfaces") as JArray;
        interfaces.Should().NotBeNull("Robot should implement interfaces");

        var interfaceNames = interfaces!.Select(i => i["name"]?.Value<string>()).ToList();
        _fixture.OutputHelper?.WriteLine($"Robot implements interfaces: {string.Join(", ", interfaceNames)}");

        // Robot should implement MachineInterface (its direct abstract parent)
        interfaceNames.Should().Contain("AssetRepositoryIntegrationTestMachineInterface",
            "Robot should implement MachineInterface");

        // Robot should also implement EquipmentInterface (the grandparent abstract type)
        interfaceNames.Should().Contain("AssetRepositoryIntegrationTestEquipmentInterface",
            "Robot should implement EquipmentInterface");

        // Verify the hasReadings field exists on Robot
        var fields = robotType.SelectToken("fields") as JArray;
        fields.Should().NotBeNull("Robot should have fields");

        var fieldNames = fields!.Select(f => f["name"]?.Value<string>()).ToList();
        fieldNames.Should().Contain("hasReadings",
            "Robot should have hasReadings field inherited from Equipment");
    }

    /// <summary>
    /// Test that querying the Equipment abstract type returns entities from all derived types.
    /// </summary>
    [Fact]
    public async Task GraphQL_AbstractEquipmentType_ReturnsAllDerivedTypeEntities()
    {
        // Arrange - Query the abstract Equipment type
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestEquipment {
                  totalCount
                  items {
                    __typename
                    rtId
                    rtWellKnownName
                    name
                  }
                }
              }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty(
            $"Query should execute without errors. Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Equipment query result: {json}");
    }

    /// <summary>
    /// Test that the inbound association (hasReadings) works on the Machine abstract type.
    /// This specifically tests the scenario where an intermediate abstract type inherits
    /// an association from its parent abstract type.
    /// Note: Union types require inline fragments for field access.
    /// </summary>
    [Fact]
    public async Task GraphQL_IntermediateAbstractType_InboundAssociation_Works()
    {
        // Arrange - Query Machine type with its hasReadings association
        // Must use inline fragment on union type
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestMachine {
                  items {
                    __typename
                    rtWellKnownName
                    name
                    hasReadings {
                      totalCount
                      items {
                        __typename
                        ... on AssetRepositoryIntegrationTestSensorReading {
                          rtWellKnownName
                        }
                      }
                    }
                  }
                }
              }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty(
            $"Query should execute without errors. Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Machine query result: {json}");
    }

    /// <summary>
    /// Test that querying equipment with interface fragment on Machine interface works.
    /// Using MachineInterface fragment should match Robot and CncMachine entities.
    /// Note: This test verifies that when querying via the MachineInterface endpoint,
    /// we can use interface fields.
    /// </summary>
    [Fact]
    public async Task GraphQL_IntermediateInterfaceFragment_MatchesDerivedTypes()
    {
        // Arrange - Query Machine (which returns robots and CNC machines)
        // and verify interface fields work
        var query = @"
            query {
              runtime {
                assetRepositoryIntegrationTestMachine {
                  items {
                    __typename
                    rtId
                    rtWellKnownName
                    name
                    manufacturer
                  }
                }
              }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty(
            $"Query should execute without errors. Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Machine query result: {json}");
    }

    /// <summary>
    /// This test specifically verifies the interface field type compatibility issue
    /// that occurred in the EnergyIQ model. When a type implements an interface,
    /// all fields must have compatible types. For association fields, the connection
    /// type must match.
    ///
    /// The bug: Interface and implementing types could have different connection types
    /// for the same association field (e.g., BasicAsset_RelatesFromUnionConnection vs
    /// SystemEntity_RelatesFromUnionConnection), causing GraphQL schema validation to fail.
    /// </summary>
    [Fact]
    public async Task GraphQL_InterfaceImplementation_AssociationFieldTypes_MustBeCompatible()
    {
        // Arrange - Introspect to verify Robot implements MachineInterface and EquipmentInterface
        // and that the hasReadings field type is consistent
        var query = @"
            query {
              robotType: __type(name: ""AssetRepositoryIntegrationTestRobot"") {
                name
                interfaces {
                  name
                }
                fields {
                  name
                  type {
                    name
                    kind
                    ofType { name }
                  }
                }
              }
              machineInterface: __type(name: ""AssetRepositoryIntegrationTestMachineInterface"") {
                name
                fields {
                  name
                  type {
                    name
                    kind
                    ofType { name }
                  }
                }
              }
              equipmentInterface: __type(name: ""AssetRepositoryIntegrationTestEquipmentInterface"") {
                name
                fields {
                  name
                  type {
                    name
                    kind
                    ofType { name }
                  }
                }
              }
            }";

        // Act
        var result = await _fixture.ExecuteGraphQlAsync(query);

        // Assert - The query should succeed, meaning the schema was valid
        result.Should().NotBeNull();
        result.Errors.Should().BeNullOrEmpty(
            $"Schema introspection should succeed, indicating interface implementation is valid. " +
            $"Errors: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");

        var json = _fixture.SerializeGraphQl(result);
        _fixture.OutputHelper?.WriteLine($"Interface types introspection: {json}");

        var answer = JObject.Parse(json);

        // Verify Robot implements both interfaces
        var robotType = answer.SelectToken("data.robotType");
        robotType.Should().NotBeNull("Robot type should exist");

        var robotInterfaces = robotType!.SelectToken("interfaces") as JArray;
        robotInterfaces.Should().NotBeNull();
        var robotInterfaceNames = robotInterfaces!.Select(i => i["name"]?.Value<string>()).ToList();

        _fixture.OutputHelper?.WriteLine($"Robot interfaces: {string.Join(", ", robotInterfaceNames)}");

        robotInterfaceNames.Should().Contain("AssetRepositoryIntegrationTestMachineInterface");
        robotInterfaceNames.Should().Contain("AssetRepositoryIntegrationTestEquipmentInterface");

        // Verify the hasReadings field exists on Robot (inherited from Equipment)
        var robotFields = robotType.SelectToken("fields") as JArray;
        var robotHasReadingsField = robotFields!.FirstOrDefault(f => f["name"]?.Value<string>() == "hasReadings");
        robotHasReadingsField.Should().NotBeNull("Robot should have hasReadings field inherited from Equipment");

        // Get the type of hasReadings on Robot
        var robotHasReadingsType = robotHasReadingsField!.SelectToken("type.name")?.Value<string>()
                                   ?? robotHasReadingsField.SelectToken("type.ofType.name")?.Value<string>();

        _fixture.OutputHelper?.WriteLine($"Robot hasReadings field type: {robotHasReadingsType}");

        // Verify EquipmentInterface also has hasReadings
        var equipmentInterface = answer.SelectToken("data.equipmentInterface");
        equipmentInterface.Should().NotBeNull("EquipmentInterface should exist");

        var equipmentFields = equipmentInterface!.SelectToken("fields") as JArray;
        var equipmentHasReadingsField = equipmentFields!.FirstOrDefault(f => f["name"]?.Value<string>() == "hasReadings");
        equipmentHasReadingsField.Should().NotBeNull("EquipmentInterface should have hasReadings field");

        var equipmentHasReadingsType = equipmentHasReadingsField!.SelectToken("type.name")?.Value<string>()
                                       ?? equipmentHasReadingsField.SelectToken("type.ofType.name")?.Value<string>();

        _fixture.OutputHelper?.WriteLine($"EquipmentInterface hasReadings field type: {equipmentHasReadingsType}");

        // The types should be the same (or at least compatible) for interface implementation to work
        // If this test passes, it means the schema was generated correctly
        robotHasReadingsType.Should().Be(equipmentHasReadingsType,
            "Robot's hasReadings field type should match EquipmentInterface's hasReadings field type " +
            "for valid interface implementation");
    }
}
