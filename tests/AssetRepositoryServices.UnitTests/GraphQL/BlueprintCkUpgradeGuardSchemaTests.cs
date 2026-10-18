using FluentAssertions;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
/// AB#6871: the tenant upgrade guard is additive on the GraphQL surface. The preview and the (dry-run) update result
/// expose <c>isBlocked</c>, <c>ckUpdateSet</c> and <c>blockers</c>; the blocker carries the same members as the library
/// status (AB#6868). Nothing was removed or made nullable.
/// </summary>
public class BlueprintCkUpgradeGuardSchemaTests
{
    [Fact]
    public void PreviewAndApplyResult_ExposeTheGuardReport()
    {
        foreach (var fields in new[]
                 {
                     new BlueprintUpdatePreviewDtoType().Fields,
                     new BlueprintApplyResultDtoType().Fields
                 })
        {
            fields.Find("isBlocked").Should().NotBeNull();
            fields.Find("ckUpdateSet").Should().NotBeNull();
            fields.Find("blockers").Should().NotBeNull();
        }
    }

    [Fact]
    public void Blocker_HasTheMembersOfTheLibraryStatus()
    {
        var blocker = new BlueprintCkUpgradeBlockerDtoType();

        foreach (var name in new[]
                 {
                     "dependent", "update", "kind", "subject", "code", "message", "dependentIsInUpdateSet",
                     "suggestedDependentUpdate", "resolveHint"
                 })
        {
            blocker.Fields.Find(name).Should().NotBeNull(name);
        }

        blocker.Fields.Find("resolveHint")!.Type.Should().Be<StringGraphType>(); // nullable
        blocker.Fields.Find("suggestedDependentUpdate")!.Type.Should().Be<StringGraphType>();
    }

    [Fact]
    public void UpdateRequestInput_HasNoForceOption()
    {
        // A GraphQL caller cannot override the guard: the override is an API / octo-cli operation for data model managers.
        new BlueprintUpdateRequestInputType().Fields.Find("forceBreakingUpdate").Should().BeNull();
    }
}
