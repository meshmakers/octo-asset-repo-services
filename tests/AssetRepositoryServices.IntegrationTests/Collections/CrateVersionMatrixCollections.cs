using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     The 5.9.10 to 6.4.6 pair of the CrateDB version matrix (AB#5490). Two CrateDB containers and an S3 store, so it runs
///     alone: no other collection runs beside it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class CrateMatrix59To6Collection
    : ICollectionFixture<Crate59StreamDataFixture>, ICollectionFixture<Crate6StreamDataFixture>
{
    public const string Name = "CrateMatrix59To6";
}

/// <summary>The 5.10.10 to 6.4.6 pair of the CrateDB version matrix (AB#5490).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class CrateMatrix510To6Collection
    : ICollectionFixture<Crate510StreamDataFixture>, ICollectionFixture<Crate6StreamDataFixture>
{
    public const string Name = "CrateMatrix510To6";
}
