using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     Test classes that run rollup recomputes with a slice target of a few rows (AB#6283), so that the copy, the sweep and the reclaim of a small rollup already run as many statements.
/// </summary>
[CollectionDefinition(Name)]
public class StreamDataRowBoundCollection : ICollectionFixture<RowBoundStreamDataFixture>
{
    public const string Name = "StreamDataRowBound";
}
