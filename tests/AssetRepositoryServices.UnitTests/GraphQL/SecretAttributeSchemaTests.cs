using FakeItEasy;
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
    public void OctoSecretState_ExposesIsSetKeyMissingAndSetAtOnly()
    {
        var type = new OctoSecretStateDtoType();

        type.Name.Should().Be("OctoSecretState");
        type.Fields.Select(f => f.Name.ToLowerInvariant()).Should()
            .BeEquivalentTo("isset", "keymissing", "setat");
        FindField(type, "isSet")!.Type.Should().Be<NonNullGraphType<BooleanGraphType>>();
        FindField(type, "keyMissing")!.Type.Should().Be<NonNullGraphType<BooleanGraphType>>();
        FindField(type, "setAt")!.Type.Should().Be<UtcDateTimeGraphType>();
    }

    [Fact]
    public void RtEntityAttribute_HasNullableSecretStateFields()
    {
        var type = new RtEntityAttributeDtoType();

        FindField(type, "secretIsSet")!.Type.Should().Be<BooleanGraphType>();
        FindField(type, "secretKeyMissing")!.Type.Should().Be<BooleanGraphType>();
        FindField(type, "secretSetAt")!.Type.Should().Be<UtcDateTimeGraphType>();
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
    public void RtEntityAttributeInput_AcceptsSecretKeyMissingAndSecretSetAt()
    {
        var type = new RtEntityAttributeDtoInputType();

        FindField(type, "secretKeyMissing")!.Type.Should().Be<BooleanGraphType>();
        FindField(type, "secretSetAt")!.Type.Should().Be<UtcDateTimeGraphType>();
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
    [InlineData("TODO_SET_PASSWORD", false)] // legacy placeholder still in storage: not set until migrated
    public void SecretState_FromRawSlotValue(string? raw, bool expected)
    {
        var state = SecretAttributeProjection.ToSecretState(raw, CreateProtector());

        state.IsSet.Should().Be(expected);
        state.KeyMissing.Should().BeFalse();
        state.SetAt.Should().BeNull();
    }

    [Fact]
    public void SecretState_FromSecretValue_IsSet_AndNeverCarriesTheValue()
    {
        var state = SecretAttributeProjection.ToSecretState(RtSecretValue.Pending(FakePlaintext), CreateProtector());

        state.IsSet.Should().BeTrue();
        state.ToString().Should().NotContain(FakePlaintext);
    }

    [Fact]
    public void SecretState_PlaceholderLookingInput_IsAnOrdinaryValue()
    {
        // Decision 2026-10-06: placeholders have no meaning on input.
        SecretAttributeProjection.ToSecretState(RtSecretValue.Pending("TODO_SET_PASSWORD"), CreateProtector())
            .IsSet.Should().BeTrue();
        SecretAttributeProjection.ToSecretState(RtSecretValue.Pending("<password>"), CreateProtector())
            .IsSet.Should().BeTrue();
    }

    [Fact]
    public void SecretState_ProtectedWithKnownKey_IsSetWithSetAt()
    {
        var setAt = new DateTime(2026, 10, 6, 8, 30, 0, DateTimeKind.Utc);

        var state = SecretAttributeProjection.ToSecretState(RtSecretValue.Protected(Envelope(KnownKeyId), setAt),
            CreateProtector());

        state.IsSet.Should().BeTrue();
        state.KeyMissing.Should().BeFalse();
        state.SetAt.Should().Be(setAt);
    }

    [Fact]
    public void SecretState_ProtectedWithUnknownKey_IsKeyMissing()
    {
        var setAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var state = SecretAttributeProjection.ToSecretState(RtSecretValue.Protected(Envelope("kx"), setAt),
            CreateProtector());

        state.IsSet.Should().BeFalse();
        state.KeyMissing.Should().BeTrue();
        state.SetAt.Should().Be(setAt);
    }

    [Fact]
    public void SecretState_CorruptStoredValue_IsNotSet()
    {
        // An enc:v2 envelope found as a legacy string can never be read.
        var state = SecretAttributeProjection.ToSecretState(Envelope(KnownKeyId), CreateProtector());

        state.IsSet.Should().BeFalse();
        state.KeyMissing.Should().BeFalse();
    }

    [Fact]
    public void GenericAttribute_CarriesAllSecretStateFields()
    {
        var setAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var dto = SecretAttributeProjection.ToAttributeDto("password", RtSecretValue.Protected(Envelope("kx"), setAt),
            CreateProtector());

        dto.AttributeName.Should().Be("password");
        dto.Value.Should().BeNull();
        dto.SecretIsSet.Should().BeFalse();
        dto.SecretKeyMissing.Should().BeTrue();
        dto.SecretSetAt.Should().Be(setAt);
    }

    private const string KnownKeyId = "k1";

    // Structurally valid envelope (nonce + tag + payload of zero bytes); never decrypted by these tests.
    private static string Envelope(string keyId)
    {
        var encoded = Convert.ToBase64String(new byte[33]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return SecretEnvelope.BuildHeaderV2(keyId) + encoded;
    }

    private static ISecretAttributeProtector CreateProtector()
    {
        var protector = A.Fake<ISecretAttributeProtector>();
        A.CallTo(() => protector.DescribeSecret(A<RtSecretValue?>._, A<SecretAccessContext?>._))
            .ReturnsLazily((RtSecretValue? value, SecretAccessContext? _) =>
                SecretValueStates.Describe(value, keyId => keyId == KnownKeyId));
        return protector;
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
