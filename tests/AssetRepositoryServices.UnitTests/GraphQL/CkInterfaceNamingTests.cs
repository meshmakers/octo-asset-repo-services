using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     F1.5-S2 (AB#5921): GraphQL names of CK interfaces keep the element version from 2 on, so <c>Named-1</c> and
///     <c>Named-2</c> (the documented evolution path of an interface) never collide in the schema.
/// </summary>
public class CkInterfaceNamingTests
{
    [Theory]
    [InlineData("System.Identity/Named-1", "SystemIdentityNamed")]
    [InlineData("System.Identity/Named-2", "SystemIdentityNamed2")]
    [InlineData("System.Identity/Named-12", "SystemIdentityNamed12")]
    [InlineData("AssetRepositoryIntegrationTest/Labeled-1", "AssetRepositoryIntegrationTestLabeled")]
    public void InterfaceName_KeepsTheVersionFromTwoOn(string rtCkInterfaceId, string expected)
    {
        new RtCkId<CkInterfaceId>(rtCkInterfaceId).GetGraphQlPascalCaseName().Should().Be(expected);
    }

    [Fact]
    public void Versions_OneAndTwo_DoNotCollide()
    {
        new RtCkId<CkInterfaceId>("Test/Named-1").GetGraphQlPascalCaseName().Should()
            .NotBe(new RtCkId<CkInterfaceId>("Test/Named-2").GetGraphQlPascalCaseName());
    }

    [Theory]
    [InlineData("System/Entity", "SystemEntity", "systemEntity")]
    [InlineData("Basic/Asset-2", "BasicAsset2", "basicAsset2")]
    public void TypeNames_FollowTheSameRule(string rtCkTypeId, string pascal, string camel)
    {
        var id = new RtCkId<CkTypeId>(rtCkTypeId);
        id.GetGraphQlPascalCaseName().Should().Be(pascal);
        id.GetGraphQlCamelCaseName().Should().Be(camel);
    }
}
