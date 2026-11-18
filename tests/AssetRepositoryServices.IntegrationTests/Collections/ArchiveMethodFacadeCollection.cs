using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>The REST and mutation facades of the archive methods, on a fixture of their own (it observes the audit).</summary>
[CollectionDefinition(Name)]
public class ArchiveMethodFacadeCollection : ICollectionFixture<ArchiveMethodFacadeFixture>
{
    public const string Name = "ArchiveMethodFacade";
}
