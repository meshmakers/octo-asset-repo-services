using FakeItEasy;
using FluentAssertions;
using global::GraphQL;
using global::GraphQL.Types;
using GraphQLParser.AST;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     AB#6932: <c>runtime.byInterface</c> exists only for tenants with a concrete type that implements a CK interface,
///     so the <c>Runtime</c> query type of every other tenant is unchanged.
/// </summary>
public class ByInterfaceSchemaTests
{
    [Fact]
    public void TenantWithoutInterfaceImplementers_HasNoByInterfaceField_AndTheUnchangedRuntimeFields()
    {
        var cache = A.Fake<IGraphTypesCache>();
        A.CallTo(() => cache.GetTypes()).Returns([]);
        A.CallTo(() => cache.GetCkInterfaceImplementers()).Returns([]);

        var query = new RuntimeModelQuery(NullLogger<RuntimeModelQuery>.Instance, cache);

        query.Fields.Select(f => f.Name).Should().Equal("RuntimeEntities", "RuntimeQuery", "TransientQuery");
        query.HasField(Statics.ByInterfaceField).Should().BeFalse();
    }

    [Fact]
    public void IsFieldSelected_IsTrueOnlyForASelectedSubField()
    {
        var context = A.Fake<IResolveFieldContext>();
        A.CallTo(() => context.SubFields).Returns(new Dictionary<string, (GraphQLField Field, FieldType FieldType)>
        {
            ["items"] = (null!, null!)
        });

        context.IsFieldSelected("items").Should().BeTrue();
        context.IsFieldSelected("totalCount").Should().BeFalse("the count is only issued when selected");
    }
}
