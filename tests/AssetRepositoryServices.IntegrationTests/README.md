# Asset Repository Services - Integration Tests

This project contains integration tests for the Octo Asset Repository Services, following the same pattern as the Persistence.SystemTests project.

## Test Structure

```
tests/AssetRepositoryServices.IntegrationTests/
├── Configuration/
│   ├── IntegrationTestConfiguration.cs    # Loads appsettings.test.json
│   └── IntegrationTestOptions.cs          # Configuration options
├── Fixtures/
│   ├── ServiceCollectionFixture.cs        # Base fixture with DI
│   ├── ConfigurationFixture.cs            # Loads configuration
│   ├── DatabaseFixture.cs                 # MongoDB Testcontainer
│   └── AssetRepoFixture.cs                # System + Test Tenant setup
├── System/
│   └── TenantContextTests.cs              # Tenant management tests
├── GraphQL/
│   ├── GraphQLTestHelper.cs               # Helper for GraphQL queries
│   └── Queries/
│       └── BasicGraphQLTests.cs           # Example GraphQL tests
├── testData/                              # Test data files
├── appsettings.test.json                  # Test configuration
└── README.md
```

## Running Tests

### Run all tests (requires Docker for MongoDB Testcontainer)
```bash
dotnet test tests/AssetRepositoryServices.IntegrationTests/ -c DebugL
```

### Run specific test class
```bash
dotnet test tests/AssetRepositoryServices.IntegrationTests/ -c DebugL --filter "FullyQualifiedName~TenantContextTests"
```

### Run with detailed output
```bash
dotnet test tests/AssetRepositoryServices.IntegrationTests/ -c DebugL --verbosity detailed
```

## Fixture Hierarchy

The test fixtures follow an inheritance hierarchy:

1. **ServiceCollectionFixture** (Base)
   - Provides `ServiceCollection` and `ServiceProvider`
   - Sets up DI container
   - Adds logging with xUnit output
   - Adds Runtime Engine with MongoDB

2. **ConfigurationFixture** (extends ServiceCollectionFixture)
   - Loads `appsettings.test.json`
   - Provides configuration access

3. **DatabaseFixture** (extends ConfigurationFixture)
   - Starts MongoDB Testcontainer
   - Configures connection to test container
   - Uses replica set for transaction support

4. **AssetRepoFixture** (extends DatabaseFixture)
   - Initializes system tenant
   - Creates test tenant
   - Provides access to test tenant context

## Writing Tests

### Basic Tenant Test

```csharp
[Collection("Sequential")]
public class MyTests(AssetRepoFixture fixture)
    : IClassFixture<AssetRepoFixture>
{
    [Fact]
    public async Task MyTest()
    {
        var systemContext = fixture.GetSystemContext();
        // Your test logic here
    }
}
```

### GraphQL Test (requires WebApplicationFactory)

```csharp
[Collection("Sequential")]
public class MyGraphQLTests(AssetRepoFixture fixture, WebApplicationFactory<Program> factory)
    : IClassFixture<AssetRepoFixture>, IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task MyGraphQLTest()
    {
        var client = factory.CreateClient();
        var graphQL = new GraphQLTestHelper(fixture);

        var query = @"query { ... }";
        var response = await graphQL.QueryAsync<MyResponse>(client, query);

        Assert.NotNull(response);
    }
}
```

## Configuration

Edit `appsettings.test.json` to configure test settings:

```json
{
  "integrationTest": {
    "tenantId": "test-tenant",
    "mongoDbImage": "mongo:8.0.15",
    "adminUser": "octo-system-admin",
    "adminUserPassword": "OctoAdmin1",
    "databaseUserPassword": "OctoUser1",
    "useDirectConnection": true
  }
}
```

## Test Containers

Tests use **Testcontainers** to run a real MongoDB instance in Docker:
- Automatically starts before tests
- Automatically stopped and cleaned up after tests
- Uses replica set for transaction support
- Each test run uses a unique container

### Requirements
- Docker must be installed and running
- Sufficient Docker resources (memory/CPU)

## Test Data

Place test data files in the `testData/` directory:
- CK model definitions (YAML/JSON)
- Sample binary files
- Test fixtures
- etc.

## Best Practices

1. **Use `[Collection("Sequential")]`** to ensure tests run sequentially (Testcontainers can be resource-intensive)
2. **Use descriptive test names** following the pattern `MethodName_Scenario_ExpectedResult`
3. **Clean up resources** in test teardown if needed
4. **Use transactions** when testing data operations
5. **Test both success and failure cases**
6. **Use FluentAssertions** for readable assertions
7. **Log to xUnit output** using the fixture's OutputHelper

## Differences from Unit Tests

Integration tests:
- ✅ Use real MongoDB (via Testcontainer)
- ✅ Test actual database operations
- ✅ Test complete request/response pipelines
- ✅ Slower execution (container startup, network)
- ✅ Require Docker
- ❌ Not isolated (shared fixtures)

Unit tests:
- ✅ Use mocks/fakes
- ✅ Fast execution
- ✅ No external dependencies
- ✅ Fully isolated
- ❌ Don't test integration points

## Troubleshooting

### Tests fail with "Cannot connect to Docker"
- Ensure Docker Desktop is running
- Check Docker daemon is accessible

### Tests are slow
- Testcontainers reuse containers when possible
- First test run downloads MongoDB image (only once)
- Consider running fewer tests in parallel

### Port conflicts
- Testcontainers automatically assigns free ports
- No manual port configuration needed

### MongoDB timeout errors
- Increase container startup timeout in `DatabaseFixture`
- Check Docker has sufficient resources

## GraphQL Tests

GraphQL tests are located in `GraphQL/Queries/`. These tests use the `SampleDataFixture` which imports the AssetRepositoryIntegrationTest construction kit model and sample runtime data.

### Current GraphQL Test Coverage

The `BasicGraphQLTests` class includes tests for:
- Querying all customers (4 customers)
- Querying all operating facilities (4 facilities)
- Querying all metering points (8 metering points)
- Filtering customers by well-known name

### Running GraphQL Tests

The GraphQL tests are currently **skipped** because they require:
1. WebApplicationFactory configuration with proper DI setup
2. Authentication bypass or test authentication handler
3. GraphQL endpoint accessibility

To enable these tests:

1. **Configure WebApplicationFactory**:
   - Set up the web host with test services
   - Configure authentication bypass for tests
   - Ensure MongoDB connection is properly configured

2. **Example WebApplicationFactory Setup**:
```csharp
public class AssetRepoWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Add test authentication
            services.AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", options => { });

            // Configure test database
            services.Configure<OctoSystemConfiguration>(config =>
            {
                config.DatabaseHost = "localhost:27017";
                // ... other config
            });
        });
    }
}
```

3. **Remove Skip Attribute**: Once configured, remove `Skip = "..."` from the test methods

## Sample Test Data

**Status**: The `SampleDataFixture` is currently **not working** because CK model import requires the models to be registered in a CK repository.

The fixture attempts to import:
- **AssetRepositoryIntegrationTest CK Model**: Customer, OperatingFacility, MeteringPoint types
- **4 Customers**: Max Mustermann, Anna Müller, Tech Solutions GmbH, Peter Weber
- **4 Operating Facilities**: Residential and commercial buildings
- **8 Metering Points**: Electricity, gas, solar, water meters
- **Associations**: Ownership relations between customers and facilities, parent-child relations between facilities and metering points

### Issue

The CK models (AssetRepositoryIntegrationTest) need to be available in a CK model repository before they can be imported by ID. This requires:
1. Setting up a CK model repository configuration
2. Registering the compiled CK models in that repository
3. Or: Creating a simpler test model directly in code (like the "Test" model in Persistence.SystemTests)

### Workaround

For now, use the `AssetRepoFixture` directly for GraphQL tests without sample data, or create entities programmatically in your tests.

## Next Steps

To complete the test infrastructure:

1. **WebApplicationFactory Setup**: Configure the web application factory to work with test containers
2. **Authentication**: Implement test authentication handler or bypass
3. **Enable GraphQL Tests**: Remove Skip attributes once infrastructure is ready
4. **Add More GraphQL Tests**:
   - Mutation tests (create, update, delete)
   - Association/navigation tests
   - Error handling tests
   - Complex filter tests
5. **Performance Tests**: Add tests measuring query performance
