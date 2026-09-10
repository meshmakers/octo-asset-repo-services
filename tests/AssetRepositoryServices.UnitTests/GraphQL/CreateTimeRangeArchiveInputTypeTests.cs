using FluentAssertions;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
/// A window period is a millisecond count, so it must ride a 64-bit scalar. Declared as
/// <c>Int</c> it rejected everything from ~25 days upwards — the Studio's own "1 month" preset
/// (2 592 000 000 ms) answered "Unable to convert '2592000000' to 'Int'", and a 92-day legacy
/// archive could only be created through ImportRt (AB#5157 review).
/// </summary>
public class CreateTimeRangeArchiveInputTypeTests
{
    private readonly CreateTimeRangeArchiveInputType _input = new();

    [Fact]
    public void PeriodMs_IsLong_SoAMonthLongWindowFits()
    {
        var field = _input.Fields.Find("periodMs");

        field.Should().NotBeNull();
        field!.Type.Should().Be<LongGraphType>();
    }

    [Fact]
    public void LongScalar_AcceptsAMonthOfMilliseconds()
    {
        // 30 days — the value the Studio's coarsest preset sends.
        new LongGraphType().ParseValue(2_592_000_000L).Should().Be(2_592_000_000L);
    }
}
