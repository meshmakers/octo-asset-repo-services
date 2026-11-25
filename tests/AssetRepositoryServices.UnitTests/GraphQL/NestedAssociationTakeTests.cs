using FakeItEasy;
using FluentAssertions;
using global::GraphQL.Builders;
using global::GraphQL.Types;
using GraphQLParser.AST;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     A nested association connection that selects <c>totalCount</c> alone asks the repository for its
///     count-only form (take 0, no target document loaded) whatever <c>first</c> says; every other selection keeps
///     <c>first</c> as the page size.
/// </summary>
public class NestedAssociationTakeTests
{
    private static IResolveConnectionContext Context(int? first, params string[] selectedFields)
    {
        var context = A.Fake<IResolveConnectionContext>();
        A.CallTo(() => context.First).Returns(first);
        A.CallTo(() => context.SubFields).Returns(selectedFields.ToDictionary(f => f,
            f => (new GraphQLField(new GraphQLName(f)), (FieldType)null!)));
        return context;
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(10)]
    public void TotalCountAlone_AsksForTheCountOnlyForm(int? first)
    {
        Context(first, "totalCount").GetAssociationTake().Should().Be(0);
        Context(first, "totalCount", "__typename").GetAssociationTake().Should().Be(0);
    }

    [Theory]
    [InlineData(null, "items")]
    [InlineData(5, "items")]
    [InlineData(null, "edges")]
    [InlineData(3, "pageInfo")]
    [InlineData(null, "aggregation")]
    public void ASelectionThatNeedsEntities_KeepsFirst(int? first, string field)
    {
        Context(first, "totalCount", field).GetAssociationTake().Should().Be(first);
        Context(first, field).GetAssociationTake().Should().Be(first);
    }
}
