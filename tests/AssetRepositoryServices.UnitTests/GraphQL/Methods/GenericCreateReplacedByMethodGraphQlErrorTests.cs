using FakeItEasy;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL.Methods;

/// <summary>
///     AB#7025: shape of the refusal of the generic create for a type whose method replaces it (ONE error,
///     <c>extensions.code</c> and <c>extensions.parameters</c> naming the type and the method, no exception data).
/// </summary>
public class GenericCreateReplacedByMethodGraphQlErrorTests
{
    [Fact]
    public void Create_CarriesStableCodeAndParametersNamingTypeAndMethod()
    {
        var error = GenericCreateReplacedByMethodGraphQlError.Create("Sample/Thing", "Sample/Thing.Provision-1");

        Statics.GraphQlGenericCreateReplacedByMethod.Should().Be("GENERIC_CREATE_REPLACED_BY_METHOD");
        error.Code.Should().Be("GENERIC_CREATE_REPLACED_BY_METHOD");
        error.Message.Should().Contain("Sample/Thing.Provision-1");
        error.Extensions.Should().NotBeNull();
        error.Extensions!["code"].Should().Be("GENERIC_CREATE_REPLACED_BY_METHOD");
        error.Extensions["parameters"].Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["ckTypeId"] = "Sample/Thing",
            ["methodId"] = "Sample/Thing.Provision-1"
        });
        error.InnerException.Should().BeNull("exception data must never reach the client");
        error.Data.Count.Should().Be(0);
    }

    [Fact]
    public void Create_IsDeterministic_ForTheSameTypeAndMethod()
    {
        var first = GenericCreateReplacedByMethodGraphQlError.Create("Sample/Thing", "Sample/Thing.Provision-1");
        var second = GenericCreateReplacedByMethodGraphQlError.Create("Sample/Thing", "Sample/Thing.Provision-1");

        second.Message.Should().Be(first.Message);
        second.Code.Should().Be(first.Code);
    }

    [Fact]
    public void TryCreate_ForATypeThatIsNotInTheCkCache_DoesNotRefuse()
    {
        var cache = A.Fake<ICkCacheService>();
        CkTypeGraph? none = null;
        A.CallTo(() => cache.TryGetRtCkType(A<string>._, A<RtCkId<CkTypeId>>._, out none)).Returns(false);

        GenericCreateReplacedByMethodGraphQlError
            .TryCreate(cache, "tenant", new RtCkId<CkTypeId>("Sample/Thing"))
            .Should().BeNull();
    }
}
