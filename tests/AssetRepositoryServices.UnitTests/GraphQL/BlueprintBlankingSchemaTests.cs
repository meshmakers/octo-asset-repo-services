using FluentAssertions;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
/// AB#6315: the blanking contract is additive on the GraphQL surface. The new input fields are
/// nullable (an old client that sends neither gets the safe default), the new output fields are
/// present on the preview and on the update result.
/// </summary>
public class BlueprintBlankingSchemaTests
{
    [Fact]
    public void UpdateRequestInput_BlankingFields_AreOptional()
    {
        var input = new BlueprintUpdateRequestInputType();

        var allow = input.Fields.Find("allowBlanking");
        allow.Should().NotBeNull();
        allow!.Type.Should().Be<BooleanGraphType>(); // nullable: omitted = false = Keep

        var pairs = input.Fields.Find("confirmedBlankings");
        pairs.Should().NotBeNull();
        pairs!.Type!.GetGenericTypeDefinition().Should().Be(typeof(ListGraphType<>)); // not wrapped in NonNull
    }

    [Fact]
    public void ConfirmationInput_NamesEntityAndAttribute()
    {
        var input = new BlueprintBlankingConfirmationInputType();

        input.Fields.Find("rtId").Should().NotBeNull();
        input.Fields.Find("attributeName").Should().NotBeNull();
    }

    [Fact]
    public void PreviewAndApplyResult_ExposeTheBlankedAttributes()
    {
        new BlueprintUpdatePreviewDtoType().Fields.Find("blankedAttributes").Should().NotBeNull();
        new BlueprintApplyResultDtoType().Fields.Find("blankedAttributes").Should().NotBeNull();

        var blanked = new BlueprintBlankedAttributeDtoType();
        foreach (var name in new[]
                 {
                     "rtId", "ckTypeId", "attributeName", "reason", "currentSummary", "incomingSummary",
                     "appliedOnUpdate"
                 })
        {
            blanked.Fields.Find(name).Should().NotBeNull(name);
        }

        // Never a value field: the report describes, it does not disclose.
        blanked.Fields.Find("currentValue").Should().BeNull();
        blanked.Fields.Find("incomingValue").Should().BeNull();
    }

    /// <summary>AB#6454: the tenant-owned lists are additive output fields, identity only.</summary>
    [Fact]
    public void PreviewAndApplyResult_ExposeTheTenantOwnedLists_IdentityOnly()
    {
        foreach (var fields in new[]
                 {
                     new BlueprintUpdatePreviewDtoType().Fields,
                     new BlueprintApplyResultDtoType().Fields
                 })
        {
            fields.Find("tenantOwnedSkipped").Should().NotBeNull();
            fields.Find("tenantOwnedStaysDeleted").Should().NotBeNull();
        }

        var entity = new BlueprintTenantOwnedEntityDtoType();
        foreach (var name in new[] { "key", "ckTypeId", "entityId", "wellKnownName" })
        {
            entity.Fields.Find(name).Should().NotBeNull(name);
        }

        entity.Fields.Should().HaveCount(4, "identity only, never attribute values");
        entity.Fields.Find("entityId")!.Type.Should().Be<StringGraphType>(); // nullable: null for stays-deleted
    }

    [Fact]
    public void BlankedAttributeReason_DescribesResetToDefault()
    {
        new BlueprintBlankedAttributeDtoType().Fields.Find("reason")!.Description.Should().Contain("ResetToDefault");
    }
}
