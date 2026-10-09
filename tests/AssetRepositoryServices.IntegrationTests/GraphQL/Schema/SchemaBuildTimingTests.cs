using System.Diagnostics;
using FluentAssertions;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Schema;

/// <summary>
///     CK v2 Phase 0, task G0 (risk R8): records how long a cold per-tenant schema build takes for the integration
///     test CK model, so the CK interface and method types added in Phase 0 can be compared against this
///     baseline. The service logs the same figure at Information level
///     ("GraphQL schema for tenant … built in … ms (…)").
/// </summary>
[Collection(GraphQlCollection.Name)]
public class SchemaBuildTimingTests
{
    private const int Runs = 3;

    private readonly GraphQlTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SchemaBuildTimingTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
        _output = output;
    }

    [Fact]
    public async Task ColdSchemaBuild_IsInitializedAndTimed()
    {
        var schemaContext = _fixture.GetService<ISchemaContext>();
        var tenantId = _fixture.GetSystemContext().TenantId;

        var timings = new List<long>();
        ISchema? schema = null;
        for (var i = 0; i < Runs; i++)
        {
            schemaContext.Invalidate(tenantId);
            var stopwatch = Stopwatch.StartNew();
            schema = await schemaContext.GetOrCreateAsync(tenantId);
            stopwatch.Stop();
            timings.Add(stopwatch.ElapsedMilliseconds);
        }

        // The schema is initialized eagerly inside the build, so the measured time is the full cost.
        schema!.Initialized.Should().BeTrue();
        schema.AllTypes.Count.Should().BeGreaterThan(0);

        _output.WriteLine(
            $"[G0 baseline] cold schema build for the integration test model: {string.Join(", ", timings)} ms " +
            $"({schema.AllTypes.Count} types)");

        // A cached schema is returned without rebuilding.
        (await schemaContext.GetOrCreateAsync(tenantId)).Should().BeSameAs(schema);
    }
}
