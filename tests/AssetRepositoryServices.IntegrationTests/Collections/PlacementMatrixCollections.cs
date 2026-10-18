using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>The placement matrix with the sweep grace period switched off (AB#6968).</summary>
[CollectionDefinition(Name)]
public class PlacementMatrixGraceOffCollection : ICollectionFixture<PlacementMatrixGraceOffFixture>
{
    public const string Name = "PlacementMatrixGraceOff";
}

/// <summary>The placement matrix with the default sweep grace period (AB#6968).</summary>
[CollectionDefinition(Name)]
public class PlacementMatrixGraceOnCollection : ICollectionFixture<PlacementMatrixGraceOnFixture>
{
    public const string Name = "PlacementMatrixGraceOn";
}
