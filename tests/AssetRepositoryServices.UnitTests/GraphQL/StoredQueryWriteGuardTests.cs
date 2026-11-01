using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     AB#8049: <see cref="StoredQueryWriteGuard.CollectPaths" /> finds every path a stored query carries, in the shapes the
///     generic and typed mutations produce (string arrays, record arrays, one record). The guard itself is covered with a real
///     schema in <c>IntegrationTests/GraphQL/CkV2/StoredQueryHiddenAttributeTests</c>.
/// </summary>
public class StoredQueryWriteGuardTests
{
    private static RtRecord Record(params (string Name, object? Value)[] members) =>
        new(new RtCkId<CkRecordId>("System/FieldFilter"), members.ToDictionary(m => m.Name, m => m.Value));

    [Fact]
    public void CollectsFiltersSortsColumnsGroupByAndSearch_WithTheirUse()
    {
        var attributes = new Dictionary<string, object?>
        {
            ["FieldFilter"] = new List<RtRecord> { Record(("AttributePath", "a"), ("ComparisonValue", "dummy")) },
            ["Sorting"] = new List<RtRecord> { Record(("AttributePath", "b")) },
            ["Columns"] = new List<object> { "c", "d" },
            ["GroupingColumns"] = new List<string> { "e" },
            ["AttributeSearchFilter"] = Record(("AttributePaths", new List<object> { "f" }), ("SearchValue", "dummy"))
        };

        var paths = StoredQueryWriteGuard.CollectPaths(name => attributes.GetValueOrDefault(name)).ToList();

        paths.Should().BeEquivalentTo(new[]
        {
            ("a", StoredQueryWriteGuard.FilterOperation),
            ("b", StoredQueryWriteGuard.SortOperation),
            ("c", StoredQueryWriteGuard.ColumnOperation),
            ("d", StoredQueryWriteGuard.ColumnOperation),
            ("e", StoredQueryWriteGuard.GroupByOperation),
            ("f", StoredQueryWriteGuard.AttributeSearchOperation)
        });
        paths.Select(p => p.Path).Should().NotContain("dummy", "comparison and search values are not paths");
    }

    [Fact]
    public void AggregationColumns_AreReadFromTheirRecords_MemberNamesCaseInsensitive()
    {
        var attributes = new Dictionary<string, object?>
        {
            ["Columns"] = new List<RtRecord> { Record(("attributePath", "x"), ("AggregationType", 1)) }
        };

        StoredQueryWriteGuard.CollectPaths(name => attributes.GetValueOrDefault(name))
            .Should().ContainSingle().Which.Should().Be(("x", StoredQueryWriteGuard.ColumnOperation));
    }

    [Fact]
    public void AQueryWithoutPaths_YieldsNothing()
    {
        StoredQueryWriteGuard.CollectPaths(_ => null).Should().BeEmpty();
    }
}
