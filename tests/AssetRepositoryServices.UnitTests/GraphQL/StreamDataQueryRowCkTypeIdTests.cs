using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
/// Aggregation / grouped-aggregation / downsampling stream-data rows have no backing entity, so
/// the engine row's <c>CkTypeId</c> is null. The GraphQL <c>ckTypeId</c> field is nullable and
/// must serialise to null for those rows. Regression guard: a previous version coalesced the null
/// into <c>new RtCkId&lt;CkTypeId&gt;("")</c>, whose <c>ElementId</c> is null — its
/// <c>IsEmpty</c>/<c>SemanticVersionedFullName</c> getter then threw a NullReferenceException
/// during serialization, surfacing to clients (e.g. the MeshBoard) as
/// "NULL_REFERENCE: Error trying to resolve field 'ckTypeId'".
/// </summary>
public class StreamDataQueryRowCkTypeIdTests
{
    private static readonly IReadOnlyList<ColumnNameMapping> NoColumns = new List<ColumnNameMapping>();

    [Fact]
    public void FromStreamDataRow_NullCkTypeId_LeavesCkTypeIdNull()
    {
        var row = new StreamDataRow
        {
            CkTypeId = null,
            Values = new Dictionary<string, object?>()
        };

        var dto = StreamDataQueryRowDto.FromStreamDataRow(row, NoColumns);

        dto.CkTypeId.Should().BeNull();
    }

    [Fact]
    public void FromStreamDataRow_PresentCkTypeId_IsPreserved()
    {
        var ckTypeId = new RtCkId<CkTypeId>("Basic.Energy/EnergyMeasurement");
        var row = new StreamDataRow
        {
            CkTypeId = ckTypeId,
            Values = new Dictionary<string, object?>()
        };

        var dto = StreamDataQueryRowDto.FromStreamDataRow(row, NoColumns);

        dto.CkTypeId.Should().Be(ckTypeId);
    }

    [Fact]
    public void RtCkIdGraph_SerializesNullToNull()
    {
        var scalar = new RtCkIdGraph<CkTypeId>();

        scalar.Serialize(null).Should().BeNull();
    }

    [Fact]
    public void RtCkIdGraph_SerializesPresentCkTypeIdToSemanticVersionedFullName()
    {
        var scalar = new RtCkIdGraph<CkTypeId>();
        var ckTypeId = new RtCkId<CkTypeId>("Basic.Energy/EnergyMeasurement");

        scalar.Serialize(ckTypeId).Should().Be(ckTypeId.SemanticVersionedFullName);
    }
}
