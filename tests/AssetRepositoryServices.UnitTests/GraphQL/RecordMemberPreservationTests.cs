using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     AB#8055: element matching of record arrays for the preservation of protected record members - the same rule as the
///     engine's Secret carry-over (record keys compare by value).
/// </summary>
public class RecordMemberPreservationTests
{
    [Theory]
    [InlineData(1, 1L, true)]
    [InlineData(2, 3L, false)]
    [InlineData("k1", "k1", true)]
    [InlineData("k1", "K1", false)]
    [InlineData(5, "5", true)]
    public void RecordKeys_CompareByValue(object a, object b, bool expected)
    {
        RecordMemberPreservation.RecordKeyEquals(a, b).Should().Be(expected);
    }

    [Fact]
    public void FindByRecordKey_ReturnsTheStoredElementWithTheSameKey()
    {
        var stored = new List<RtRecord> { Record("k1", "a"), Record("k2", "b") };

        RecordMemberPreservation.FindByRecordKey("Key", Record("k2", null), stored)!
            .Attributes["Ref"].Should().Be("b");
    }

    [Fact]
    public void FindByRecordKey_ReturnsNull_ForANewElementOrAMissingKey()
    {
        var stored = new List<RtRecord> { Record("k1", "a") };

        RecordMemberPreservation.FindByRecordKey("Key", Record("k9", null), stored).Should().BeNull();
        RecordMemberPreservation.FindByRecordKey("Key", new RtRecord(), stored).Should().BeNull();
        RecordMemberPreservation.FindByRecordKey("Key", Record("k1", null), []).Should().BeNull();
    }

    private static RtRecord Record(string key, string? reference)
    {
        var record = new RtRecord();
        record.SetAttributeRawValue("Key", key);
        if (reference != null)
        {
            record.SetAttributeRawValue("Ref", reference);
        }

        return record;
    }
}
