using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

public class StreamDataQueryValidationTests
{
    private readonly StreamDataFieldResolver _fieldResolver = new(["Voltage", "Temperature"]);

    // No CK type in the cache: the Hidden check (CK v2 E-M2) finds nothing; it is covered by the integration tests.
    private readonly StreamDataHiddenGuard _noHidden = CreateNoHiddenGuard();

    private static StreamDataHiddenGuard CreateNoHiddenGuard()
    {
        var cache = A.Fake<ICkCacheService>();
        A.CallTo(() => cache.GetRtCkType(A<string>._, A<RtCkId<CkTypeId>>._))
            .Throws(new InvalidOperationException("not in cache"));
        return new StreamDataHiddenGuard(cache, "tenant", new RtCkId<CkTypeId>("Test/Type"));
    }

    [Fact]
    public void AllValidFields_DoesNotThrow()
    {
        var act = () => StreamDataFieldValidation.ValidateStreamDataFields(
            _fieldResolver,
            ["Voltage", "Temperature"],
            ["Timestamp"],
            ["Voltage"], _noHidden);

        act.Should().NotThrow();
    }

    [Fact]
    public void UnknownColumn_ThrowsWithFieldName()
    {
        var act = () => StreamDataFieldValidation.ValidateStreamDataFields(
            _fieldResolver,
            ["Voltage", "NonExistent"],
            null,
            null, _noHidden);

        act.Should().Throw<OctoGraphQLException>()
            .WithMessage("*NonExistent*");
    }

    [Fact]
    public void UnknownSortField_ThrowsWithFieldName()
    {
        var act = () => StreamDataFieldValidation.ValidateStreamDataFields(
            _fieldResolver,
            null,
            ["BadSort"],
            null, _noHidden);

        act.Should().Throw<OctoGraphQLException>()
            .WithMessage("*BadSort*");
    }

    [Fact]
    public void UnknownFilterField_ThrowsWithFieldName()
    {
        var act = () => StreamDataFieldValidation.ValidateStreamDataFields(
            _fieldResolver,
            null,
            null,
            ["UnknownFilter"], _noHidden);

        act.Should().Throw<OctoGraphQLException>()
            .WithMessage("*UnknownFilter*");
    }

    [Fact]
    public void MultipleUnknownsAcrossAllCategories_ThrowsSingleExceptionListingAll()
    {
        var act = () => StreamDataFieldValidation.ValidateStreamDataFields(
            _fieldResolver,
            ["BadCol"],
            ["BadSort"],
            ["BadFilter"], _noHidden);

        act.Should().Throw<OctoGraphQLException>()
            .WithMessage("*BadCol*")
            .WithMessage("*BadSort*")
            .WithMessage("*BadFilter*");
    }

    [Fact]
    public void NullAndEmptyInputs_DoNotThrow()
    {
        var act = () => StreamDataFieldValidation.ValidateStreamDataFields(
            _fieldResolver,
            null,
            null,
            null, _noHidden);

        act.Should().NotThrow();

        var act2 = () => StreamDataFieldValidation.ValidateStreamDataFields(
            _fieldResolver,
            [],
            [],
            [], _noHidden);

        act2.Should().NotThrow();
    }

    [Fact]
    public void DefaultFields_AreValid()
    {
        var act = () => StreamDataFieldValidation.ValidateStreamDataFields(
            _fieldResolver,
            ["Timestamp", "RtId"],
            ["Timestamp"],
            ["RtId"], _noHidden);

        act.Should().NotThrow();
    }

    [Fact]
    public void CaseInsensitiveMatch_IsValid()
    {
        var act = () => StreamDataFieldValidation.ValidateStreamDataFields(
            _fieldResolver,
            ["voltage", "TEMPERATURE", "timestamp"],
            ["rtId"],
            ["voltage"], _noHidden);

        act.Should().NotThrow();
    }
}
