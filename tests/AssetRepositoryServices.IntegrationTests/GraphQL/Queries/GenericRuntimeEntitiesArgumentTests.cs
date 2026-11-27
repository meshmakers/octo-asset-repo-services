using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Queries;

[Collection(GraphQlCollection.Name)]
public class GenericRuntimeEntitiesArgumentTests
{
    private readonly GraphQlTestFixture _fixture;

    public GenericRuntimeEntitiesArgumentTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    [Theory]
    [InlineData("first: 30")]
    [InlineData("ckId: null, first: 30")]
    public async Task RuntimeEntitiesWithoutCkId_IsRefusedAsInvalidArgumentNamingCkId(string arguments)
    {
        var result = await _fixture.ExecuteGraphQlAsync(
            $"query {{ runtime {{ runtimeEntities({arguments}) {{ totalCount items {{ rtId ckTypeId }} }} }} }}");

        result.Errors.Should().ContainSingle();
        var error = result.Errors![0];
        error.Code.Should().Be("INVALID_ARGUMENT");
        error.Extensions!["argument"].Should().Be("ckId");
        error.Message.Should().Contain("ckId");
    }
}
