using FakeItEasy;
using FluentAssertions;
using global::GraphQL;
using global::GraphQL.Types;
using GraphQLParser.AST;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     AB#8895: a connection selection of <c>totalCount</c> alone is answered by a server-side count. Anything that
///     needs the entities (items, edges, page info, aggregations) keeps the item query.
/// </summary>
public class TotalCountOnlySelectionTests
{
    private static IResolveFieldContext Context(params (string ResponseKey, string FieldName)[] selection)
    {
        var context = A.Fake<IResolveFieldContext>();
        var subFields = selection.ToDictionary(s => s.ResponseKey,
            s => (new GraphQLField(new GraphQLName(s.FieldName)), (FieldType)null!));
        A.CallTo(() => context.SubFields).Returns(subFields);
        return context;
    }

    [Fact]
    public void TotalCountAlone_IsCountOnly()
    {
        Context(("totalCount", "totalCount")).IsTotalCountOnlySelected().Should().BeTrue();
    }

    [Fact]
    public void AliasedTotalCount_IsCountOnly()
    {
        Context(("howMany", "totalCount")).IsTotalCountOnlySelected().Should().BeTrue("an alias does not change the field");
    }

    [Fact]
    public void TotalCountWithTypename_IsCountOnly()
    {
        Context(("totalCount", "totalCount"), ("__typename", "__typename")).IsTotalCountOnlySelected()
            .Should().BeTrue("__typename needs no entity");
    }

    [Theory]
    [InlineData("items")]
    [InlineData("edges")]
    [InlineData("pageInfo")]
    [InlineData("aggregation")]
    [InlineData("fieldAggregations")]
    public void TotalCountWithAFieldThatNeedsEntities_IsNotCountOnly(string field)
    {
        Context(("totalCount", "totalCount"), (field, field)).IsTotalCountOnlySelected().Should().BeFalse();
    }

    [Fact]
    public void AliasOfItems_IsNotCountOnly()
    {
        Context(("totalCount", "totalCount"), ("totalCount2", "items")).IsTotalCountOnlySelected()
            .Should().BeFalse("the AST field name decides, not the response key");
    }

    [Fact]
    public void NoTotalCount_IsNotCountOnly()
    {
        Context(("__typename", "__typename")).IsTotalCountOnlySelected().Should().BeFalse();
        Context().IsTotalCountOnlySelected().Should().BeFalse();
    }

    [Fact]
    public void NoSubFields_IsNotCountOnly()
    {
        var context = A.Fake<IResolveFieldContext>();
        A.CallTo(() => context.SubFields).Returns(null);

        context.IsTotalCountOnlySelected().Should().BeFalse();
    }
}
