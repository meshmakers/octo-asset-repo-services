using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;

/// <summary>
///     Test classes that run the stream data stack under a one-second statement budget (AB#6283). A collection of its own: the budget is a property of the fixture's services and would make the other stream data tests depend on the speed of the machine.
/// </summary>
[CollectionDefinition(Name)]
public class StreamDataStatementBudgetCollection : ICollectionFixture<StatementBudgetStreamDataFixture>
{
    public const string Name = "StreamDataStatementBudget";
}
