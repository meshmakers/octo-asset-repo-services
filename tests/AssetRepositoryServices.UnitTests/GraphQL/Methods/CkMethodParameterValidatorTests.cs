using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL.Methods;

/// <summary>AB#7026: the gateway's parameter check names members and fixed reasons, never a value.</summary>
public class CkMethodParameterValidatorTests
{
    private const string MethodId = "Sample/Account.Change-1";

    private static CkMethodDto Definition() => new()
    {
        MethodId = "Change-1",
        Parameters =
        [
            new CkMethodParameterDto { Name = "newPassword", ValueType = AttributeValueTypesDto.String, Sensitive = true },
            new CkMethodParameterDto { Name = "count", ValueType = AttributeValueTypesDto.Int, IsOptional = true },
            new CkMethodParameterDto { Name = "flags", ValueType = AttributeValueTypesDto.IntArray, IsOptional = true },
            new CkMethodParameterDto { Name = "on", ValueType = AttributeValueTypesDto.Boolean, IsOptional = true }
        ]
    };

    [Theory]
    [InlineData("{\"newPassword\":\"x\"}")]
    [InlineData("{\"NEWPASSWORD\":\"x\",\"count\":3,\"flags\":[1,2],\"on\":true}")]
    [InlineData("{\"newPassword\":\"x\",\"count\":null}")]
    public void ValidParameters_PassWithoutError(string json) =>
        Assert.Null(CkMethodParameterValidator.Validate(MethodId, Definition(), json));

    [Theory]
    [InlineData("{}", "newPassword", "missing-required")]
    [InlineData("{\"newPassword\":null}", "newPassword", "missing-required")]
    [InlineData("{\"newPassword\":\"x\",\"other\":1}", "other", "unknown-member")]
    [InlineData("{\"newPassword\":\"x\",\"count\":\"3\"}", "count", "wrong-type")]
    [InlineData("{\"newPassword\":\"x\",\"flags\":1}", "flags", "wrong-type")]
    [InlineData("{\"newPassword\":\"x\",\"on\":\"true\"}", "on", "wrong-type")]
    [InlineData("{\"newPassword\":5}", "newPassword", "wrong-type")]
    public void InvalidParameters_AreRefusedWithTheMemberAndAFixedReason(string json, string field, string reason)
    {
        var error = CkMethodParameterValidator.Validate(MethodId, Definition(), json);

        Assert.NotNull(error);
        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, error.Code);
        Assert.Equal(MethodId, error.Parameters[CkMethodErrorParameters.MethodId]);
        Assert.Equal(field, error.Parameters[CkMethodErrorParameters.Field]);
        Assert.Equal(reason, error.Parameters[CkMethodErrorParameters.Reason]);
    }

    [Fact]
    public void SeveralProblems_AreReportedTogether_SortedByMember()
    {
        var error = CkMethodParameterValidator.Validate(MethodId, Definition(), "{\"z\":1,\"count\":\"x\"}");

        Assert.NotNull(error);
        Assert.Equal("count,newPassword,z", error.Parameters[CkMethodErrorParameters.Field]);
        Assert.Equal("missing-required,unknown-member,wrong-type", error.Parameters[CkMethodErrorParameters.Reason]);
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("not json CANARY")]
    public void ParametersThatAreNoJsonObject_AreRefused_WithoutEchoingThem(string json)
    {
        var error = CkMethodParameterValidator.Validate(MethodId, Definition(), json);

        Assert.NotNull(error);
        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, error.Code);
        Assert.DoesNotContain("CANARY", string.Join(",", error.Parameters.Values));
    }

    [Fact]
    public void NoParametersDeclared_EmptyObjectAndMissingJsonAreValid()
    {
        var definition = new CkMethodDto { MethodId = "Touch-1" };

        Assert.Null(CkMethodParameterValidator.Validate(MethodId, definition, "{}"));
        Assert.Null(CkMethodParameterValidator.Validate(MethodId, definition, null));
    }
}
