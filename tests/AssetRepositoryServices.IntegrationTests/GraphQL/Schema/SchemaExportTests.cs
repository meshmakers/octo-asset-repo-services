using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Schema;

/// <summary>
///     Prints the GraphQL schema (SDL) of the integration-test tenant. With <c>OCTO_SCHEMA_EXPORT_PATH</c> set the SDL
///     is written to that file — the hand-off for frontend codegen when the CK meta API changes (CK v2 F1.5-S3,
///     hand-off H-A1). Without the variable the test only checks that the schema prints.
/// </summary>
[Collection(GraphQlCollection.Name)]
public class SchemaExportTests(GraphQlTestFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task Schema_PrintsAsSdl()
    {
        fixture.OutputHelper = output;
        var schema = await fixture.GetService<ISchemaContext>().GetOrCreateAsync(fixture.GetSystemContext().TenantId);

        var sdl = schema.Print();

        sdl.Should().Contain("type CkInterface").And.Contain("type CkMethod");
        var path = Environment.GetEnvironmentVariable("OCTO_SCHEMA_EXPORT_PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            await File.WriteAllTextAsync(path, "# This file was generated. Do not edit manually.\n\n" + sdl,
                TestContext.Current.CancellationToken);
            output.WriteLine($"Schema written to {path}");
        }
    }
}
