using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Services.StreamData;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

public class StreamDataQueryValidationTests
{
    private readonly StreamDataFieldResolver _fieldResolver = new(["Voltage", "Temperature"]);

    [Fact]
    public void AllValidFields_DoesNotThrow()
    {
        var act = () => StreamDataQuery.ValidateStreamDataFields(
            _fieldResolver,
            ["Voltage", "Temperature"],
            ["Timestamp"],
            ["Voltage"]);

        act.Should().NotThrow();
    }

    [Fact]
    public void UnknownColumn_ThrowsWithFieldName()
    {
        var act = () => StreamDataQuery.ValidateStreamDataFields(
            _fieldResolver,
            ["Voltage", "NonExistent"],
            null,
            null);

        act.Should().Throw<OctoGraphQLException>()
            .WithMessage("*NonExistent*");
    }

    [Fact]
    public void UnknownSortField_ThrowsWithFieldName()
    {
        var act = () => StreamDataQuery.ValidateStreamDataFields(
            _fieldResolver,
            null,
            ["BadSort"],
            null);

        act.Should().Throw<OctoGraphQLException>()
            .WithMessage("*BadSort*");
    }

    [Fact]
    public void UnknownFilterField_ThrowsWithFieldName()
    {
        var act = () => StreamDataQuery.ValidateStreamDataFields(
            _fieldResolver,
            null,
            null,
            ["UnknownFilter"]);

        act.Should().Throw<OctoGraphQLException>()
            .WithMessage("*UnknownFilter*");
    }

    [Fact]
    public void MultipleUnknownsAcrossAllCategories_ThrowsSingleExceptionListingAll()
    {
        var act = () => StreamDataQuery.ValidateStreamDataFields(
            _fieldResolver,
            ["BadCol"],
            ["BadSort"],
            ["BadFilter"]);

        act.Should().Throw<OctoGraphQLException>()
            .WithMessage("*BadCol*")
            .WithMessage("*BadSort*")
            .WithMessage("*BadFilter*");
    }

    [Fact]
    public void NullAndEmptyInputs_DoNotThrow()
    {
        var act = () => StreamDataQuery.ValidateStreamDataFields(
            _fieldResolver,
            null,
            null,
            null);

        act.Should().NotThrow();

        var act2 = () => StreamDataQuery.ValidateStreamDataFields(
            _fieldResolver,
            [],
            [],
            []);

        act2.Should().NotThrow();
    }

    [Fact]
    public void DefaultFields_AreValid()
    {
        var act = () => StreamDataQuery.ValidateStreamDataFields(
            _fieldResolver,
            ["Timestamp", "RtId"],
            ["Timestamp"],
            ["RtId"]);

        act.Should().NotThrow();
    }

    [Fact]
    public void CaseInsensitiveMatch_IsValid()
    {
        var act = () => StreamDataQuery.ValidateStreamDataFields(
            _fieldResolver,
            ["voltage", "TEMPERATURE", "timestamp"],
            ["rtId"],
            ["voltage"]);

        act.Should().NotThrow();
    }
}
