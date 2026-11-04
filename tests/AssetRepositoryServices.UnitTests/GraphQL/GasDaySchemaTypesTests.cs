using FluentAssertions;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
/// AB#7361 — the gas-day fields are additive and optional: a day start is a millisecond count (64-bit scalar like the
/// other <c>*Ms</c> fields), and no existing caller has to send it.
/// </summary>
public class GasDaySchemaTypesTests
{
    [Fact]
    public void CreateRollupArchiveInput_DayStartOffsetMs_IsAnOptionalLong()
    {
        var field = new CreateRollupArchiveInputType().Fields.Find("dayStartOffsetMs");

        field.Should().NotBeNull();
        field!.Type.Should().Be<LongGraphType>("nullable, so existing createRollupArchive callers are unaffected");
    }

    [Fact]
    public void RollupArchiveInfo_ReturnsTheDayStart()
    {
        var field = new RollupArchiveInfoDtoType().Fields.Find("dayStartOffsetMs");

        field.Should().NotBeNull();
        field!.Type.Should().Be<LongGraphType>("null means a midnight day");
    }

    [Fact]
    public void ResolveSeriesQueryInput_TakesTheDayStart_AsMillisecondsOrBinOrigin()
    {
        var type = new ResolveSeriesQueryInputType();

        type.Fields.Find("dayStartOffsetMs")!.Type.Should().Be<LongGraphType>();
        type.Fields.Find("binOrigin")!.Type.Should().Be<StringGraphType>();
    }
}
