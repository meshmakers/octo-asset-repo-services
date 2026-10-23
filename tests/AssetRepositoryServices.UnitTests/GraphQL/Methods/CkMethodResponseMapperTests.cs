using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL.Methods;

/// <summary>
///     AB#5670: a gateway response becomes the typed field value or ONE error with <c>extensions.code</c> and
///     <c>extensions.parameters</c>; a declared handler error keeps its own code; the result follows the declared value type.
/// </summary>
public class CkMethodResponseMapperTests
{
    private const string Tenant = "meshtest";
    private static readonly Guid Invocation = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly ICkCacheService _cache = A.Fake<ICkCacheService>();

    public CkMethodResponseMapperTests()
    {
        var mode = new CkEnumGraph(new CkId<CkEnumId>("Sample-1.0.0/Mode-1"), false, false,
            [new Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.CkEnumValueDto { Key = 0, Name = "Unknown" }, new Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.CkEnumValueDto { Key = 2, Name = "Maintenance" }],
            "mode");
        CkEnumGraph? enumGraph = mode;
        A.CallTo(() => _cache.TryGetRtCkEnum(Tenant, A<RtCkId<CkEnumId>>._, out enumGraph)).Returns(true);

        var specId = new CkId<CkRecordId>("Sample-1.0.0/Spec-1");
        var attributes = new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>
        {
            [new CkId<CkAttributeId>("Sample-1.0.0/SpecName-1")] = Attribute("SpecName", AttributeValueTypesDto.String),
            [new CkId<CkAttributeId>("Sample-1.0.0/SpecValue-1")] = Attribute("SpecValue", AttributeValueTypesDto.Double),
            [new CkId<CkAttributeId>("Sample-1.0.0/SpecMode-1")] = Attribute("SpecMode", AttributeValueTypesDto.Enum,
                enumId: new CkId<CkEnumId>("Sample-1.0.0/Mode-1"))
        };
        CkRecordGraph? recordGraph = new CkRecordGraph(specId, false, false, [], null, [], [], attributes, "spec");
        A.CallTo(() => _cache.TryGetRtCkRecord(Tenant, A<RtCkId<CkRecordId>>._, out recordGraph)).Returns(true);
    }

    [Theory]
    [InlineData(CkMethodErrorCodes.MethodForbidden)]
    [InlineData(CkMethodErrorCodes.MethodTargetNotFound)]
    [InlineData(CkMethodErrorCodes.MethodValidationFailed)]
    [InlineData(CkMethodErrorCodes.MethodHandlerNotAvailable)]
    [InlineData(CkMethodErrorCodes.MethodTimeout)]
    [InlineData(CkMethodErrorCodes.MethodHandlerFailed)]
    [InlineData("PASSWORD_POLICY_VIOLATION")]
    public void FailedResponse_IsOneErrorWithTheCodeAndParameters(string code)
    {
        var response = CkMethodResponse.Failed(Invocation, "h1",
            CkMethodError.Create(code, ("methodId", "Sample/Thing.Do-1"), ("field", "newPassword")));

        var outcome = CkMethodResponseMapper.Map(response, Method(), _cache, Tenant);

        outcome.Value.Should().BeNull();
        outcome.Error.Should().NotBeNull();
        outcome.Error!.Code.Should().Be(code, "a declared handler error keeps its own code");
        outcome.Error.Extensions!["code"].Should().Be(code);
        outcome.Error.Extensions["parameters"].Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["methodId"] = "Sample/Thing.Do-1",
            ["field"] = "newPassword"
        });
        outcome.Error.Extensions.Should().NotContainKey("details");
    }

    [Fact]
    public void FailedResponse_WithDetails_CarriesThemInTheExtensions()
    {
        var error = new CkMethodError
        {
            Code = CkMethodErrorCodes.MethodValidationFailed,
            Details =
            [
                new CkMethodErrorDetail
                {
                    Code = "TOO_SHORT",
                    Parameters = new Dictionary<string, string> { ["field"] = "newPassword" }
                }
            ]
        };

        var outcome = CkMethodResponseMapper.Map(CkMethodResponse.Failed(Invocation, "h1", error), Method(), _cache, Tenant);

        var details = outcome.Error!.Extensions!["details"].Should().BeAssignableTo<IList<object>>().Subject;
        details.Should().HaveCount(1);
    }

    [Fact]
    public void FailedResponse_WithoutErrorObject_IsAHandlerFailureWithCorrelationId()
    {
        var response = new CkMethodResponse { InvocationId = Invocation, HandlerInstanceId = "h1", Success = false };

        var outcome = CkMethodResponseMapper.Map(response, Method(), _cache, Tenant);

        outcome.Error!.Code.Should().Be(CkMethodErrorCodes.MethodHandlerFailed);
        outcome.Error.Extensions!["parameters"].Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["methodId"] = "Sample/Thing.Do-1",
            ["correlationId"] = Invocation.ToString("D")
        });
    }

    [Fact]
    public void Success_WithoutDeclaredResult_ReturnsTheInvocationIdOnly()
    {
        var outcome = CkMethodResponseMapper.Map(CkMethodResponse.Succeeded(Invocation, "h1", "{\"ignored\":1}"),
            Method(), _cache, Tenant);

        outcome.Error.Should().BeNull();
        outcome.Value!.InvocationId.Should().Be(Invocation);
        outcome.Value.Value.Should().BeNull();
    }

    [Theory]
    [InlineData(AttributeValueTypesDto.String, "\"hello\"", "hello")]
    [InlineData(AttributeValueTypesDto.Int, "42", 42)]
    [InlineData(AttributeValueTypesDto.Boolean, "true", true)]
    [InlineData(AttributeValueTypesDto.Int64, "9000000000", 9000000000L)]
    public void Success_ScalarResult_IsTyped(AttributeValueTypesDto type, string json, object expected)
    {
        var outcome = CkMethodResponseMapper.Map(CkMethodResponse.Succeeded(Invocation, "h1", json),
            Method(result: new CkMethodResultDto { ValueType = type }), _cache, Tenant);

        outcome.Error.Should().BeNull();
        outcome.Value!.Value.Should().Be(expected);
    }

    [Fact]
    public void Success_DoubleResult_IsADecimal_AndArraysAreLists()
    {
        var number = CkMethodResponseMapper.Map(CkMethodResponse.Succeeded(Invocation, "h1", "1.5"),
            Method(result: new CkMethodResultDto { ValueType = AttributeValueTypesDto.Double }), _cache, Tenant);
        var list = CkMethodResponseMapper.Map(CkMethodResponse.Succeeded(Invocation, "h1", "[\"a\",\"b\"]"),
            Method(result: new CkMethodResultDto { ValueType = AttributeValueTypesDto.StringArray }), _cache, Tenant);

        number.Value!.Value.Should().Be(1.5m);
        list.Value!.Value.Should().BeEquivalentTo(new List<string> { "a", "b" });
    }

    [Theory]
    [InlineData("\"Maintenance\"")]
    [InlineData("\"MAINTENANCE\"")]
    [InlineData("2")]
    public void Success_EnumResult_IsTheKey(string json)
    {
        var outcome = CkMethodResponseMapper.Map(CkMethodResponse.Succeeded(Invocation, "h1", json),
            Method(result: new CkMethodResultDto
            {
                ValueType = AttributeValueTypesDto.Enum,
                ValueCkEnumId = new CkId<CkEnumId>("Sample-1.0.0/Mode-1")
            }), _cache, Tenant);

        outcome.Value!.Value.Should().Be(2);
    }

    [Fact]
    public void Success_RecordResult_IsARecordWithTheAttributes()
    {
        var outcome = CkMethodResponseMapper.Map(
            CkMethodResponse.Succeeded(Invocation, "h1", "{\"specName\":\"Torque\",\"specValue\":3.5,\"specMode\":\"Maintenance\"}"),
            Method(result: new CkMethodResultDto
            {
                ValueType = AttributeValueTypesDto.Record,
                ValueCkRecordId = new CkId<CkRecordId>("Sample-1.0.0/Spec-1")
            }), _cache, Tenant);

        outcome.Error.Should().BeNull();
        var dto = outcome.Value!.Value.Should().BeOfType<RtRecordDto>().Subject;
        var record = dto.UserContext.Should().BeOfType<RtRecord>().Subject;
        record.GetAttributeValueOrDefault("SpecName").Should().Be("Torque");
        record.GetAttributeValueOrDefault("SpecValue").Should().Be(3.5);
        record.GetAttributeValueOrDefault("SpecMode").Should().Be(2);
    }

    [Theory]
    [InlineData("\"not a number\"")]
    [InlineData("not json")]
    public void Success_WithAResultThatDoesNotFitTheDeclaration_IsAHandlerFailure_WithoutTheValue(string json)
    {
        var outcome = CkMethodResponseMapper.Map(CkMethodResponse.Succeeded(Invocation, "h1", json),
            Method(result: new CkMethodResultDto { ValueType = AttributeValueTypesDto.Int }), _cache, Tenant);

        outcome.Value.Should().BeNull();
        outcome.Error!.Code.Should().Be(CkMethodErrorCodes.MethodHandlerFailed);
        outcome.Error.Message.Should().NotContain("not a number");
    }

    [Fact]
    public void Accepted_AsynchronousCall_ReturnsTheInvocationIdWithoutResult()
    {
        var response = CkMethodResponse.Accepted(Invocation, "h1", new CkMethodOperationHandle { OperationId = "op1" });

        var outcome = CkMethodResponseMapper.Map(response,
            Method(result: new CkMethodResultDto { ValueType = AttributeValueTypesDto.Int }), _cache, Tenant);

        outcome.Error.Should().BeNull();
        outcome.Value!.Value.Should().BeNull();
    }

    private static CkMethodGraph Method(CkMethodResultDto? result = null) =>
        new(new CkId<CkTypeId>("Sample-1.0.0/Thing-1"),
            new CkMethodDto { MethodId = "Do-1", Kind = CkMethodKindDto.Instance, Result = result });

    private static CkTypeAttributeGraph Attribute(string name, AttributeValueTypesDto type, CkId<CkEnumId>? enumId = null) =>
        new(new CkId<CkAttributeId>($"Sample-1.0.0/{name}-1"), name, null, type, null, enumId, null, null, null, true, null);
}
