using FakeItEasy;
using FluentAssertions;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Microsoft.Extensions.Options;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     CK v2 Phase 0 (AB#5668, contract §4.1): <c>OctoBuilder.Attribute</c> is the single chokepoint for attribute
///     fields of entity, interface, input, update and record types. Hidden never appears; MethodOnly is readable but
///     not part of the generic input; ReadOnly is unchanged in Phase 0.
/// </summary>
public class CkAttributeAccessSchemaTests
{
    private static readonly IOptions<OctoAssetRepositoryServicesOptions> Options =
        Microsoft.Extensions.Options.Options.Create(new OctoAssetRepositoryServicesOptions());

    private readonly IGraphTypesCache _cache = A.Fake<IGraphTypesCache>();

    [Theory]
    [InlineData(CkAttributeAccessDto.ReadWrite, true, true)]
    [InlineData(CkAttributeAccessDto.ReadOnly, true, true)]
    [InlineData(CkAttributeAccessDto.MethodOnly, true, false)]
    [InlineData(CkAttributeAccessDto.Hidden, false, false)]
    public void Attribute_RespectsAccess(CkAttributeAccessDto access, bool inOutput, bool inInput)
    {
        var attribute = Attribute("Secretish", access);

        var output = new ObjectGraphType<RtEntityDto>();
        OctoBuilder<RtEntityDto>.Create(output, Options).Attribute(_cache, attribute, isInputType: false);
        var outputInterface = new InterfaceGraphType<RtEntityDto>();
        OctoBuilder<RtEntityDto>.Create(outputInterface, Options)
            .Attribute(_cache, attribute, isInputType: false, isInterface: true);
        var input = new InputObjectGraphType<RtEntityDto>();
        OctoBuilder<RtEntityDto>.Create(input, Options).Attribute(_cache, attribute, isInputType: true);
        var recordOutput = new ObjectGraphType<RtRecordDto>();
        OctoBuilder<RtRecordDto>.Create(recordOutput, Options).Attribute(_cache, attribute, isInputType: false);
        var recordInput = new InputObjectGraphType<RtRecordDto>();
        OctoBuilder<RtRecordDto>.Create(recordInput, Options).Attribute(_cache, attribute, isInputType: true);

        output.HasField("Secretish").Should().Be(inOutput);
        outputInterface.HasField("Secretish").Should().Be(inOutput);
        recordOutput.HasField("Secretish").Should().Be(inOutput);
        input.HasField("Secretish").Should().Be(inInput);
        recordInput.HasField("Secretish").Should().Be(inInput);
    }

    [Fact]
    public void CkTypeAttribute_ExposesAccessAsString()
    {
        var type = new CkTypeAttributeDtoType();

        var field = type.Fields.Find("access");
        field.Should().NotBeNull();
        field!.Type.Should().Be<NonNullGraphType<StringGraphType>>();
    }

    [Fact]
    public void AccessQueryGuard_IsHidden_OnlyForHidden()
    {
        AccessQueryGuard.IsHidden(null).Should().BeFalse();
        AccessQueryGuard.IsHidden(Attribute("A", CkAttributeAccessDto.ReadWrite)).Should().BeFalse();
        AccessQueryGuard.IsHidden(Attribute("A", CkAttributeAccessDto.ReadOnly)).Should().BeFalse();
        AccessQueryGuard.IsHidden(Attribute("A", CkAttributeAccessDto.MethodOnly)).Should().BeFalse();
        AccessQueryGuard.IsHidden(Attribute("A", CkAttributeAccessDto.Hidden)).Should().BeTrue();
    }

    [Fact]
    public void HiddenAttributeAccessException_CarriesTheContractCodesAndMessage()
    {
        var write = HiddenAttributeAccessException.NotWritable("PasswordHash", "System.Identity/User",
            CkAttributeAccessDto.Hidden);
        write.Code.Should().Be("ATTRIBUTE_NOT_WRITABLE");
        write.Message.Should().Be(
            "Attribute 'PasswordHash' of 'System.Identity/User' is not writable via generic mutations (access: Hidden).");

        var read = HiddenAttributeAccessException.NotQueryable("passwordHash", "System.Identity/User", "sort");
        read.Code.Should().Be("ATTRIBUTE_NOT_QUERYABLE");
    }

    [Theory]
    [InlineData("members.someType[passwordHash='AQAA']->name", "members.someType[passwordHash=…]->name")]
    [InlineData("a.t[x=1].b.u[y='z']->c", "a.t[x=…].b.u[y=…]->c")]
    [InlineData("items[0].name", "items[0].name")]
    public void AccessErrors_RedactSelectorValues(string path, string expected)
    {
        // F1.5-S4 (AB#5923): access errors never carry values.
        var error = HiddenAttributeAccessException.NotQueryable(path, "Test/Type", "query column");

        error.AttributePath.Should().Be(expected);
        error.Message.Should().Contain(expected).And.NotContain("AQAA");
    }

    private static CkTypeAttributeGraph Attribute(string name, CkAttributeAccessDto access)
    {
        return new CkTypeAttributeGraph(new CkId<CkAttributeId>("Test/" + name), name, null,
            AttributeValueTypesDto.String, null, null, null, null, null, true, null)
        {
            Access = access
        };
    }
}
