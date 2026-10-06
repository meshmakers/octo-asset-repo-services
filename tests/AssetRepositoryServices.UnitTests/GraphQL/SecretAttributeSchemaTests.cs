using FluentAssertions;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     AB#5528 (concept §4): the GraphQL surface of Secret attributes - read state type, generic projection
///     field, clear input field, the untyped-scalar safety net and the refusal error code.
/// </summary>
public class SecretAttributeSchemaTests
{
    private const string FakePlaintext = "fake-test-secret-value";

    // Field names are converted to camelCase when the schema is initialised; compare case-insensitively.
    private static FieldType? FindField(IComplexGraphType type, string name)
    {
        return type.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OctoSecretState_ExposesOnlyIsSet()
    {
        var type = new OctoSecretStateDtoType();

        type.Name.Should().Be("OctoSecretState");
        type.Fields.Should().ContainSingle();
        FindField(type, "isSet")!.Type.Should().Be<NonNullGraphType<BooleanGraphType>>();
    }

    [Fact]
    public void RtEntityAttribute_HasNullableSecretIsSet()
    {
        var type = new RtEntityAttributeDtoType();

        var field = FindField(type, "secretIsSet");
        field.Should().NotBeNull();
        field!.Type.Should().Be<BooleanGraphType>();
        FindField(type, "value").Should().NotBeNull();
    }

    [Fact]
    public void RtEntityAttributeInput_AcceptsSecretIsSet()
    {
        var type = new RtEntityAttributeDtoInputType();

        FindField(type, "secretIsSet").Should().NotBeNull(
            "a client must be able to send back what it read without a validation error");
    }

    [Fact]
    public void GenericUpdateInput_HasClearSecretAttributes()
    {
        var type = new RtEntityDtoGenericUpdateType();

        var field = FindField(type, "clearSecretAttributes");
        field.Should().NotBeNull();
        field!.Type.Should().Be<ListGraphType<NonNullGraphType<StringGraphType>>>();
    }

    [Fact]
    public void TypedUpdateInput_HasClearSecretAttributes()
    {
        var itemType = new InputObjectGraphType { Name = "SomeTypeInput" };
        var type = new UpdateMutationDtoType<RtEntityDto>(itemType);

        var field = FindField(type, "clearSecretAttributes");
        field.Should().NotBeNull();
        field!.Type.Should().Be<ListGraphType<NonNullGraphType<StringGraphType>>>();
        FindField(type, "item").Should().NotBeNull();
        FindField(type, "rtId").Should().NotBeNull();
    }

    [Fact]
    public void SimpleScalar_NeverSerializesASecret()
    {
        var scalar = new SimpleScalarType();

        scalar.Serialize(RtSecretValue.Pending(FakePlaintext)).Should().BeNull();
        scalar.Serialize(RtSecretValue.LegacyPlaintext(FakePlaintext)).Should().BeNull();

        var list = scalar.Serialize(new List<object?> { "a", RtSecretValue.Pending(FakePlaintext) });
        list.Should().BeAssignableTo<IEnumerable<object?>>()
            .Which.Should().BeEquivalentTo(new object?[] { "a", null });
    }

    [Fact]
    public void SimpleScalar_LeavesOtherValuesUnchanged()
    {
        var scalar = new SimpleScalarType();
        var list = new List<object?> { 1, "x" };

        scalar.Serialize("text").Should().Be("text");
        scalar.Serialize(42).Should().Be(42);
        scalar.Serialize(list).Should().BeSameAs(list);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("legacy clear text", true)]
    public void SecretState_FromRawSlotValue(string? raw, bool expected)
    {
        SecretAttributeProjection.ToSecretState(raw).IsSet.Should().Be(expected);
    }

    [Fact]
    public void SecretState_FromSecretValue_IsSet_AndNeverCarriesTheValue()
    {
        var state = SecretAttributeProjection.ToSecretState(RtSecretValue.Pending(FakePlaintext));

        state.IsSet.Should().BeTrue();
        state.ToString().Should().NotContain(FakePlaintext);
    }

    [Fact]
    public void NotQueryableException_MapsToStableCode()
    {
        var exception = new SecretAttributeNotQueryableException("password", "sort", "Test/Credential");

        ResolveConnectionContextExtensions.TryCreateSecretNotQueryableError(exception, out var error)
            .Should().BeTrue();

        error!.Code.Should().Be("SecretAttributeNotQueryable");
        error.Code.Should().Be(Statics.GraphQlSecretAttributeNotQueryable);
        error.Extensions!["attributePath"].Should().Be("password");
        error.Extensions["operation"].Should().Be("sort");
    }

    [Fact]
    public void NotQueryableException_IsFoundWhenWrapped()
    {
        var wrapped = new InvalidOperationException("outer",
            new SecretAttributeNotQueryableException("endpoints.token", "aggregation", "Test/Credential"));

        ResolveConnectionContextExtensions.TryCreateSecretNotQueryableError(wrapped, out var error)
            .Should().BeTrue();
        error!.Extensions!["attributePath"].Should().Be("endpoints.token");
    }

    [Fact]
    public void OtherExceptions_AreNotMapped()
    {
        ResolveConnectionContextExtensions.TryCreateSecretNotQueryableError(new InvalidOperationException("x"),
            out var error).Should().BeFalse();
        error.Should().BeNull();
    }
}
