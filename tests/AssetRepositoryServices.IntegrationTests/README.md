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
│   ├── ConfigurationFixture.cs            # Loads configuration, per-fixture database/tenant names
│   ├── SharedMongoDbContainer.cs          # One MongoDB Testcontainer per test process
│   ├── SharedCrateDbContainer.cs          # One CrateDB Testcontainer per test process
│   ├── SharedContainerLifetime.cs         # Assembly fixture stopping both after the last collection
│   ├── CrateDbTestCluster.cs              # Two-node CrateDB cluster for tables with a replica copy (AB#6647)
│   ├── ReplicatedStreamDataFixture.cs     # StreamDataFixture on that cluster, 1 shard + 1 replica
│   ├── DatabaseFixture.cs                 # Binds a fixture to the shared MongoDB container
│   └── AssetRepoFixture.cs                # System + Test Tenant setup
├── System/
│   └── TenantContextTests.cs              # Tenant management tests
├── Tenant/
│   └── BlueprintUpdateBlankingFlowTests.cs # AB#6315: preview -> apply -> confirm flow of BlueprintsController
│                                           # against a real tenant (TestBlueprints/BlankingFlowBp, own fixture)
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
   - Acquires the process-wide MongoDB Testcontainer from `SharedMongoDbContainer`
   - Configures the connection to it and points the fixture at its own GUID-suffixed system
     database / system tenant
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

Tests use **Testcontainers** to run a real MongoDB instance in Docker, plus a CrateDB instance for
the stream-data collections:
- Started lazily by the first fixture that needs them
- **One MongoDB and one CrateDB container per test process** (AB#5118) - `SharedMongoDbContainer` /
  `SharedCrateDbContainer` hand the same container to every fixture, and `SharedContainerLifetime`
  (an assembly fixture) stops them after the last collection
- Fixtures isolate themselves on those shared servers **by name**, not by server: each fixture
  instance has its own GUID-suffixed `SystemDatabaseName`, `SystemTenantId` (which also becomes its
  CrateDB schema) and `TestTenantId`
- **One exception: the `StreamDataReplicated` collection** (AB#6647) starts a CrateDB cluster of its
  own - `CrateDbTestCluster`, two nodes of the same image on a Docker network of their own, 512 MB
  heap each (measured: about 0.6 GiB resident per node, formed in 5-6 s) - because a table only
  gets a replica copy on a second node, and CrateDB before 5.10.12 loses deterministic column
  defaults on the replica copy only. `ReplicatedStreamDataFixture` creates every archive table
  with one shard and one replica; it stops the cluster and removes the network when the collection
  is done. Put a test there only when it needs the replica copy; assert the replica is assigned
  (`sys.shards`) before the write and read the result several times on every node - a read is
  served by either copy
- MongoDB uses a replica set for transaction support
- Container names are GUID-suffixed per `dotnet test` run, so concurrent runs on the same Docker
  host get their own pair of containers rather than colliding

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

## Rollup consistency tests (AB#6968)

These need Docker (MongoDB and a single-node CrateDB as Testcontainers, shared per test process).

```bash
# Validate -> queued repair -> host tick -> Done -> validate ok, against a real CrateDB
dotnet test tests/AssetRepositoryServices.IntegrationTests/ -c DebugL --filter "FullyQualifiedName~RollupRepairEndToEndTests"

# A rollup dumped to a file:// staging area inside the CrateDB container and loaded into a fresh rollup
dotnet test tests/AssetRepositoryServices.IntegrationTests/ -c DebugL --filter "FullyQualifiedName~RollupDumpRestoreSmokeTests"

# The placement matrix: grace period off/on x repeat rule off/on x placement of the pointer flip and the sweep
dotnet test tests/AssetRepositoryServices.IntegrationTests/ -c DebugL --filter "FullyQualifiedName~PlacementMatrix"
```

- **Placement matrix** (`StreamData/PlacementMatrixCases.cs`): a recompute (new late points in the source, a new generation, a pointer
  flip, with the grace period zero also the immediate sweep) is placed before the dump, between the copy of the generation map and the
  copy of the data, after the data, and after the dump; flip+sweep is placed between and after. The dump is restored into a fresh
  rollup and measured: empty windows, windows that differ from the source *at the time of the map read*, orphans above the pointer
  and pointers without rows after the validator ran. `PlacementMatrixGraceOffTests` / `PlacementMatrixGraceOnTests` run the same
  cells on a host with `StreamData:Recompute:SweepGracePeriod` zero or the default 2 h. Pass criterion: grace on and repeat rule on
  give 0 / 0 / 0 in every placement (`RepeatRuleOn`). The other cells are characterisation tests (`RepeatRuleOff_Characterisation`,
  and `RepeatRuleOn` without grace): they assert only that the dump and the load ran and record the observed result.
  The table is written to `placement-matrix-results.md` next to the test assembly (`bin/DebugL/net10.0/`).
- **Staging area**: `file://` inside the CrateDB container. CrateDB does not create the directories of `COPY ... TO DIRECTORY`, so the
  tests create the slice folders through a container exec and make them writable (`RollupDumpRestoreSmokeTests.PrepareStagingAsync`);
  the cluster setting `stats.jobs_log_filter` is set once per process (`RollupTestKit`), without it the copy refuses to run.
- **Recompute tests** that assert the state right after a recompute (only the generation the pointer selects is left) run with a sweep grace
  period of zero: `ImmediateSweepStreamDataFixture` (collection `StreamDataImmediateSweep`) or the grace setting in their own fixture
  (`RowBoundStreamDataFixture`, `StatementBudgetStreamDataFixture`). Every other test uses the default of 2 h; production configuration is untouched.

## CrateDB version matrix (AB#5490)

Proves that an archive dumped on a CrateDB 5.x restores on a 6.x CrateDB with identical data. Needs Docker; the images are pulled on
the first run (about 3 GB: `crate:5.9.10`, `crate:5.10.10`, `crate:6.4.6`, `chrislusf/seaweedfs:4.48`). **No kind cluster, no cloud
store**: a SeaweedFS container (the store of the kind infrastructure, same arguments) and two CrateDB containers share one Docker network,
and `COPY ... TO / FROM 's3://...'` runs on the CrateDB nodes exactly as in a cluster (format 2). The store credential is generated per
process and never printed.

```bash
# Everything of the matrix (both pairs), separately from the rest of the suite
dotnet test tests/AssetRepositoryServices.IntegrationTests/ -c DebugL --filter "Category=CrateVersionMatrix"

# One version pair locally (two CrateDB containers + the store)
dotnet test tests/AssetRepositoryServices.IntegrationTests/ -c DebugL --filter "FullyQualifiedName~CrateVersionMatrix59To6Tests"
dotnet test tests/AssetRepositoryServices.IntegrationTests/ -c DebugL --filter "FullyQualifiedName~CrateVersionMatrix510To6Tests"

# Only the small-data smoke of a pair (20 rows of the fixture archive)
dotnet test tests/AssetRepositoryServices.IntegrationTests/ -c DebugL --filter "Category=CrateVersionMatrix&Matrix=Smoke"

# The suite without the matrix
dotnet test tests/AssetRepositoryServices.IntegrationTests/ -c DebugL --filter "Category!=CrateVersionMatrix"
```

- **Pairs**: 5.9.10 to 6.4.6 and 5.10.10 to 6.4.6 (`Collections/CrateVersionMatrixCollections.cs`; each pair is one collection that runs alone,
  `DisableParallelization`). The exact tags are in `Fixtures/CrateVersionMatrixInfrastructure.cs`; `OCTO_CRATE_MATRIX_IMAGE_59`,
  `OCTO_CRATE_MATRIX_IMAGE_510` and `OCTO_CRATE_MATRIX_IMAGE_6` replace them for a try with another patch release.
  `OCTO_CRATE_MATRIX_ROWS` sets the rows of the seeded archives (default 20,000).
- **Cases** (`CrateVersions/CrateVersionMatrixCases.cs`, written once, one derived class per pair):

  | Case | What it proves |
  |---|---|
  | `Smoke_...` | the fixture archive (20 rows) dumped on 5.x loads on 6.x with identical aggregates |
  | `Format2_ASeededArchive_...` | 20,000 rows with NULLs in `voltage` / `current`: row count, sum, min, max and count of non-null of every column identical; NULL in a column without DEFAULT stays NULL |
  | `Format2_EveryColumnType_...` | every archive column type (boolean, int, int64, enum, double, string, date-time, date-time-offset, time-span, geo point, string array, int array, record, record array) with NULLs: aggregates and array / record / geo details identical |
  | `Format2_ANullInAColumnWithADefault_...` | a NULL in a column with a DEFAULT (`rtcreationdatetime`) is restored as the default, not as NULL (AB#6647); the same on both series |
  | `Format1_ARowBackup...` | the rows written as the bot's tenant dump writes them (NDJSON) and read back like its import reads them restore through `ImportRowsAsync` with identical data; a control import on the source version shows the row path behaves the same on every version |
  | `Rollup_ARecomputedRange_...` | a rollup recomputed twice (generation 1 live) dumped on 5.x reads the same values on 6.x and validates ok |

- **Disk factor**: every full case prints one line per pair, for example
  `[format 2] 5.10.10 -> 6.4.6: 20,000 rows, copy 0.7 s (1 attempt(s)); disk 5.10.10 2,013,325 B (100.7 B/row), 6.4.6 3,689,251 B (184.5 B/row), disk factor 1.83`.
  The disk size is the sum of the primary shard sizes in `sys.shards` after `OPTIMIZE TABLE`; the factor is target / source. Read the lines with
  `--logger "console;verbosity=detailed"`.
- **Runtime** (macOS, Docker Desktop, images already pulled, idle machine): the matrix takes about 30 s of test time and 50 s wall clock for
  both pairs (4 CrateDB containers one after the other, 1 store). That is the budget to add to the asset-repo CI job (90 min, agent with one
  DinD sidecar); the first run on an agent adds the image pulls.
- **Notes**: the probe type `ColumnTypeProbe` in `tests/AssetRepositoryIntegrationTestCkModel` has an attribute for every column type
  except the geo point, which is added to the table by hand because the CK source generator does not compile a `GeospatialPoint`
  attribute (`GetAttributeGeometryObjectValueOrDefault<TValue>`, CS0411).
