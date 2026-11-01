using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     Credential guard regression tests against the real System.Identity model (<see cref="IdentityCredentialGuardFixture" />).
/// </summary>
[CollectionDefinition(Name)]
public class IdentityCredentialGuardCollection : ICollectionFixture<IdentityCredentialGuardFixture>
{
    public const string Name = "IdentityCredentialGuard";
}

/// <summary>
///     Credential guard regression tests of the blueprint seed path (<see cref="IdentitySeedBlueprintFixture" />).
/// </summary>
[CollectionDefinition(Name)]
public class IdentitySeedBlueprintCollection : ICollectionFixture<IdentitySeedBlueprintFixture>
{
    public const string Name = "IdentitySeedBlueprint";
}
