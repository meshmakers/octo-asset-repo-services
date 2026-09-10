using FluentAssertions;
using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Xunit;
using CkAttributeDto = Meshmakers.Octo.Communication.Contracts.DataTransferObjects.CkAttributeDto;
using CkTypeAttributeDto = Meshmakers.Octo.Communication.Contracts.DataTransferObjects.CkTypeAttributeDto;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     AB#5191: attribute ownership must be visible in the construction-kit GraphQL surface, otherwise an
///     operator cannot tell which attributes an InstallBlueprint --force overwrites and which belong to the
///     tenant — the blind spot that let a re-apply reset an Anthropic API key and finAPI credentials.
/// </summary>
public class CkAttributeOwnershipSchemaTests
{
    private readonly CkAttributeDtoType _ckAttribute = new();
    private readonly CkTypeAttributeDtoType _ckTypeAttribute = new();

    [Fact]
    public void AttributeOwnershipEnum_ExposesAllFourOwnerships()
    {
        var enumType = new AttributeOwnershipDtoType();

        enumType.Name.Should().Be("AttributeOwnership");
        enumType.Values.Select(v => v.Name).Should()
            .BeEquivalentTo("SEED_OWNED", "TENANT_OWNED", "RUNTIME_STATE", "SECRET");
    }

    [Fact]
    public void CkAttribute_ExposesDefinitionOwnership()
    {
        var field = _ckAttribute.Fields.Find("ownership");

        field.Should().NotBeNull();
        field!.Type.Should().Be<NonNullGraphType<AttributeOwnershipDtoType>>();
        field.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CkTypeAttribute_ExposesBothTheApplyingOwnershipAndTheRawOverride()
    {
        // Both, deliberately: the override answers "was this overridden here?", the applying value answers
        // "what happens on a re-apply?". Making a caller combine two nullable fields is how the wrong
        // answer gets computed.
        var applying = _ckTypeAttribute.Fields.Find("ownership");
        var declaredOverride = _ckTypeAttribute.Fields.Find("ownershipOverride");

        applying.Should().NotBeNull();
        applying!.Type.Should().Be<NonNullGraphType<AttributeOwnershipDtoType>>();

        declaredOverride.Should().NotBeNull();
        declaredOverride!.Type.Should().Be<AttributeOwnershipDtoType>();
    }

    [Fact]
    public void ExistingFields_AreUntouched()
    {
        // The change is additive: a client querying today's fields must be unaffected.
        foreach (var fieldName in new[]
                 {
                     "CkAttributeId", "AttributeValueType", "CkRecord", "CkEnum", "Description", "MetaData",
                     "DefaultValues"
                 })
        {
            _ckAttribute.Fields.Find(fieldName).Should().NotBeNull(fieldName);
        }

        foreach (var fieldName in new[]
                 {
                     "CkAttributeId", "AttributeName", "AttributeValueType", "AutoCompleteValues",
                     "AutoIncrementReference", "IsOptional", "Attribute"
                 })
        {
            _ckTypeAttribute.Fields.Find(fieldName).Should().NotBeNull(fieldName);
        }
    }

    [Fact]
    public void NoBooleanRuntimeStateField_IsExposed()
    {
        // AB#5191: the interface deliberately exposes ONLY the enum. The boolean alias conflates two
        // different questions, and this test exists so re-adding it is a decision rather than an
        // oversight. If a predicate is ever wanted, it must be NAMED for the question it answers
        // (isPreservedOnReapply / isExcludedFromExport) — see the note in CkAttributeDtoType.
        _ckAttribute.Fields.Find("isRuntimeState").Should().BeNull();
        _ckTypeAttribute.Fields.Find("isRuntimeState").Should().BeNull();
    }

    [Theory]
    [InlineData(AttributeOwnershipDto.SeedOwned, false, false)]
    [InlineData(AttributeOwnershipDto.TenantOwned, true, false)]
    [InlineData(AttributeOwnershipDto.RuntimeState, true, true)]
    [InlineData(AttributeOwnershipDto.Secret, true, true)]
    public void Ownership_AnswersBothQuestionsSeparately(AttributeOwnershipDto ownership, bool expectedPreserved,
        bool expectedExcludedFromExport)
    {
        // The reason the enum replaced the boolean: TenantOwned is preserved on a re-apply AND still
        // exported, which a single flag cannot express. Pinned here because the GraphQL surface now
        // hands callers the enum and nothing else — this table is what they resolve it against.
        ownership.IsPreservedOnUpsert().Should().Be(expectedPreserved);
        ownership.IsExcludedFromExport().Should().Be(expectedExcludedFromExport);
    }

    [Fact]
    public async Task CkTypeAttribute_ApplyingOwnership_IsTheOverrideWhenDeclared()
    {
        var source = new OwnershipAwareCkTypeAttributeDto
        {
            // What the construction-kit compiler resolved: the override won over a SeedOwned definition.
            Ownership = AttributeOwnershipDto.Secret,
            OwnershipOverride = AttributeOwnershipDto.Secret,
            CkAttributeId = new CkId<CkAttributeId>("Test-1.0.0/ClientId-1"),
            AttributeName = "clientId",
            Attribute = new OwnershipAwareCkAttributeDto
            {
                Ownership = AttributeOwnershipDto.SeedOwned,
                CkAttributeId = new CkId<CkAttributeId>("Test-1.0.0/ClientId-1")
            }
        };

        (await ResolveAsync(_ckTypeAttribute, "ownership", source)).Should().Be(AttributeOwnershipDto.Secret);
        (await ResolveAsync(_ckTypeAttribute, "ownershipOverride", source)).Should()
            .Be(AttributeOwnershipDto.Secret);
    }

    [Fact]
    public async Task CkTypeAttribute_OverrideIsNull_WhenTheAssignmentInheritsTheDefinition()
    {
        var source = new OwnershipAwareCkTypeAttributeDto
        {
            Ownership = AttributeOwnershipDto.RuntimeState,
            OwnershipOverride = null,
            CkAttributeId = new CkId<CkAttributeId>("Test-1.0.0/ApiKey-1"),
            AttributeName = "apiKey"
        };

        (await ResolveAsync(_ckTypeAttribute, "ownership", source)).Should()
            .Be(AttributeOwnershipDto.RuntimeState);
        (await ResolveAsync(_ckTypeAttribute, "ownershipOverride", source)).Should().BeNull();
    }

    private static async Task<object?> ResolveAsync<TSource>(IComplexGraphType graphType, string fieldName,
        TSource source)
    {
        var field = graphType.Fields.Find(fieldName);
        field.Should().NotBeNull(fieldName);
        field!.Resolver.Should().NotBeNull(fieldName);

        return await field.Resolver!.ResolveAsync(new ResolveFieldContext<TSource> { Source = source });
    }
}
