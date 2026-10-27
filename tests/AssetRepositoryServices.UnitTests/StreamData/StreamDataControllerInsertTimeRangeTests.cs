using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AssetRepositoryServices.UnitTests.StreamData;

/// <summary>
///     AB#5185: <c>StreamDataController.InsertTimeRange</c> must hand CLR-typed attribute values to the
///     repository. ASP.NET Core binds the body with the System.Text.Json web defaults, which leave the
///     values of an object-typed dictionary as <see cref="JsonElement" />; Npgsql cannot bind those, so
///     every point carrying attribute values answered 500. The DTO now materializes the values through
///     the shared <c>RtAttributesConverter</c>. The tests therefore bind a real JSON body with the MVC
///     defaults and assert what reaches <see cref="IStreamDataRepository.InsertTimeRangeAsync" />.
/// </summary>
public class StreamDataControllerInsertTimeRangeTests
{
    private const string TenantId = "maco";
    private const string CkTypeId = "Basic.Energy/EnergyMeasurement";

    /// <summary>The options MVC binds <c>[FromBody]</c> with when the service registers none of its own.</summary>
    private static readonly JsonSerializerOptions MvcBodyOptions = new JsonOptions().JsonSerializerOptions;

    private readonly OctoObjectId _archiveRtId = OctoObjectId.GenerateNewId();
    private readonly OctoObjectId _rtId = OctoObjectId.GenerateNewId();
    private readonly IStreamDataRepository _repository = A.Fake<IStreamDataRepository>();
    private readonly StreamDataController _controller;

    public StreamDataControllerInsertTimeRangeTests()
    {
        var tenantContext = A.Fake<ITenantContext>();
        A.CallTo(() => tenantContext.GetStreamDataRepository()).Returns(_repository);
        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.FindTenantContextAsync(TenantId)).Returns(tenantContext);

        _controller = new StreamDataController(
            A.Fake<ILogger<StreamDataController>>(),
            systemContext,
            A.Fake<IHostApplicationLifetime>());
    }

    [Fact]
    public async Task InsertTimeRange_HandsClrTypedAttributeValuesToTheRepository()
    {
        var body = $$"""
            [{
              "rtId": "{{_rtId}}",
              "ckTypeId": "{{CkTypeId}}",
              "from": "2026-08-31T22:00:00Z",
              "to": "2026-09-01T22:00:00Z",
              "rtWellKnownName": "meter-1",
              "attributes": {
                "Amount.Value": 10.5,
                "Amount.Count": 10,
                "Amount.Total": 2147483648,
                "Amount.Unit": "kWh",
                "Amount.Estimated": true,
                "Amount.Comment": null
              }
            }]
            """;

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(body));

        result.Should().BeOfType<OkObjectResult>();
        var (archiveRtId, points) = CapturedInsert();
        archiveRtId.Should().Be(_archiveRtId);
        var point = points.Should().ContainSingle().Subject;
        point.RtId.Should().Be(_rtId);
        point.CkTypeId.Should().Be(new RtCkId<CkTypeId>(CkTypeId));
        point.From.Should().Be(new DateTime(2026, 8, 31, 22, 0, 0, DateTimeKind.Utc));
        point.To.Should().Be(new DateTime(2026, 9, 1, 22, 0, 0, DateTimeKind.Utc));
        point.RtWellKnownName.Should().Be("meter-1");

        point.Attributes.Values.Should().NotContain(value => value is JsonElement,
            "Npgsql cannot bind a JsonElement without an explicit type");
        point.Attributes["Amount.Value"].Should().BeOfType<double>().Which.Should().Be(10.5);
        point.Attributes["Amount.Count"].Should().BeOfType<int>().Which.Should().Be(10);
        point.Attributes["Amount.Total"].Should().BeOfType<long>().Which.Should().Be(2147483648L);
        point.Attributes["Amount.Unit"].Should().Be("kWh");
        point.Attributes["Amount.Estimated"].Should().Be(true);
        point.Attributes["Amount.Comment"].Should().BeNull();
    }

    [Fact]
    public async Task InsertTimeRange_KeepsIntegersIntegral_AndOnlyRealsBecomeDouble()
    {
        // Acceptance criterion: integer attribute values are stored as integers, not silently widened
        // to floating point. A JSON real that happens to be integral stays a double, as in the pipeline path.
        var body = $$"""
            [{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}",
               "from": "2026-08-31T22:00:00Z", "to": "2026-09-01T22:00:00Z",
               "attributes": { "Count": 0, "Negative": -42, "IntegralReal": 2.0 } }]
            """;

        await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(body));

        var attributes = CapturedInsert().Points.Single().Attributes;
        attributes["Count"].Should().BeOfType<int>().Which.Should().Be(0);
        attributes["Negative"].Should().BeOfType<int>().Which.Should().Be(-42);
        attributes["IntegralReal"].Should().BeOfType<double>().Which.Should().Be(2.0);
    }

    [Fact]
    public async Task InsertTimeRange_TreatsMissingOrNullAttributesAsEmpty()
    {
        var body = $$"""
            [{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}",
               "from": "2026-08-31T22:00:00Z", "to": "2026-09-01T22:00:00Z" },
             { "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}",
               "from": "2026-09-01T22:00:00Z", "to": "2026-09-02T22:00:00Z", "attributes": null }]
            """;

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(body));

        result.Should().BeOfType<OkObjectResult>();
        var points = CapturedInsert().Points;
        points.Should().HaveCount(2);
        points.Should().OnlyContain(p => p.Attributes != null && p.Attributes.Count == 0);
    }

    // ---- AB#5356 / AB#7593 / AB#7596: origin, reason and writer --------------------------------------------

    private string OnePoint(string extra) => $$"""
        [{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}",
           "from": "2026-08-31T22:00:00Z", "to": "2026-09-01T22:00:00Z",
           "attributes": { "Amount.Value": 1.5 } {{extra}} }]
        """;

    private void SignInAs(params System.Security.Claims.Claim[] claims)
    {
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "Bearer"))
            }
        };
    }

    [Fact]
    public async Task InsertTimeRange_WithoutOrigin_WritesAnAutomatedRowWithoutWriter()
    {
        SignInAs(new System.Security.Claims.Claim("sub", "user-1"));

        await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(OnePoint("")));

        var point = CapturedInsert().Points.Single();
        point.Origin.Should().Be(ArchiveRowOrigin.Automated);
        point.ManualWrittenBy.Should().BeNull();
        point.ManualReason.Should().BeNull();
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("Manual")]
    public async Task InsertTimeRange_ManualOrigin_RecordsTheTokenSubjectAsWriterAndTheReasonAsGiven(string origin)
    {
        SignInAs(new System.Security.Claims.Claim("sub", "user-1"));

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(),
            Bind(OnePoint($", \"origin\": \"{origin}\", \"reason\": \"meter exchanged\"")));

        result.Should().BeOfType<OkObjectResult>();
        var point = CapturedInsert().Points.Single();
        point.Origin.Should().Be(ArchiveRowOrigin.Manual);
        point.ManualWrittenBy.Should().Be("user-1");
        point.ManualReason.Should().Be("meter exchanged");
    }

    [Fact]
    public async Task InsertTimeRange_ManualOrigin_RecordsTheReadableUserNameFromTheToken()
    {
        // AB#7647: the writer is the readable name of the verified user, not the opaque subject id.
        SignInAs(new System.Security.Claims.Claim("sub", "8a4c1f00-0000-4000-8000-000000000001"),
            new System.Security.Claims.Claim("preferred_username", "jane.doe"),
            new System.Security.Claims.Claim("name", "Jane Doe"));

        await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(OnePoint(", \"origin\": \"manual\"")));

        CapturedInsert().Points.Single().ManualWrittenBy.Should().Be("jane.doe");
    }

    [Fact]
    public async Task InsertTimeRange_ManualOrigin_WithoutAPreferredUsername_RecordsTheName()
    {
        SignInAs(new System.Security.Claims.Claim("sub", "8a4c1f00-0000-4000-8000-000000000001"),
            new System.Security.Claims.Claim("name", "Jane Doe"));

        await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(OnePoint(", \"origin\": \"manual\"")));

        CapturedInsert().Points.Single().ManualWrittenBy.Should().Be("Jane Doe");
    }

    [Fact]
    public async Task InsertTimeRange_ManualOrigin_ANameInTheBodyIsNeverTheWriter()
    {
        SignInAs(new System.Security.Claims.Claim("sub", "user-1"), new System.Security.Claims.Claim("preferred_username", "jane.doe"));

        await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(),
            Bind(OnePoint(", \"origin\": \"manual\", \"manualWrittenBy\": \"mallory\", \"writer\": \"mallory\"")));

        CapturedInsert().Points.Single().ManualWrittenBy.Should().Be("jane.doe");
    }

    [Fact]
    public async Task InsertTimeRange_ManualOriginWithoutReason_IsAccepted()
    {
        SignInAs(new System.Security.Claims.Claim("sub", "user-1"));

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(OnePoint(", \"origin\": \"manual\"")));

        result.Should().BeOfType<OkObjectResult>();
        CapturedInsert().Points.Single().ManualReason.Should().BeNull();
    }

    [Fact]
    public async Task InsertTimeRange_AClientCredentialsToken_IsRecordedByItsClientId()
    {
        SignInAs(new System.Security.Claims.Claim("client_id", "import-service"));

        await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(OnePoint(", \"origin\": \"manual\"")));

        CapturedInsert().Points.Single().ManualWrittenBy.Should().Be("import-service");
    }

    [Theory]
    [InlineData("Manuel")]
    [InlineData("1")]
    [InlineData("corrected")]
    public async Task InsertTimeRange_AnInvalidOrigin_IsRefusedAndNamesTheValidValues(string origin)
    {
        SignInAs(new System.Security.Claims.Claim("sub", "user-1"));

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(),
            Bind(OnePoint($", \"origin\": \"{origin}\"")));

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value!.ToString().Should().Contain(origin).And.Contain("'manual' or 'automated'");
        Fake.GetCalls(_repository).Should().NotContain(c => c.Method.Name == nameof(IStreamDataRepository.InsertTimeRangeAsync));
    }

    [Fact]
    public async Task InsertTimeRange_AReasonOnAnAutomatedPoint_IsRefused()
    {
        SignInAs(new System.Security.Claims.Claim("sub", "user-1"));

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(OnePoint(", \"reason\": \"why\"")));

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task InsertTimeRange_AManualWriteWithoutCallerIdentity_IsRefused()
    {
        SignInAs();

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(OnePoint(", \"origin\": \"manual\"")));

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void InsertTimeRange_RequiresTheReadWritePolicy_SoACallerWithoutWritePermissionCannotWriteManualRows()
    {
        var method = typeof(StreamDataController).GetMethod(nameof(StreamDataController.InsertTimeRange))!;
        var policies = method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Select(a => a.Policy);

        policies.Should().Contain(Meshmakers.Octo.Backend.AssetRepositoryServices.AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy);
    }

    // ---- AB#7598: the write result ---------------------------------------------------------------------------

    [Fact]
    public async Task InsertTimeRange_AnswersWithTheWriteResult()
    {
        SignInAs(new System.Security.Claims.Claim("sub", "user-1"));
        A.CallTo(() => _repository.InsertTimeRangeAsync(A<OctoObjectId>._, A<IEnumerable<TimeRangeStreamDataPoint>>._, A<CancellationToken>._))
            .Returns(new StreamDataWriteResult(1,
            [
                new StreamDataSkippedRow(_rtId, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 3, 1, 0, 15, 0, DateTimeKind.Utc),
                    ArchiveRowOrigin.Automated, StreamDataSkipReason.ManualValueProtected)
            ]));

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(OnePoint("")));

        var body = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<StreamDataWriteResultRestDto>().Subject;
        body.Stored.Should().Be(1);
        body.Skipped.Should().Be(1);
        body.SkippedByReason.Should().ContainKey("ManualValueProtected").WhoseValue.Should().Be(1);
        body.SkippedRows.Should().ContainSingle().Which.Reason.Should().Be("ManualValueProtected");
    }

    [Fact]
    public async Task InsertTimeRange_AManualRowNotStored_Answers409WithTheResult()
    {
        SignInAs(new System.Security.Claims.Claim("sub", "user-1"));
        var writeResult = new StreamDataWriteResult(0,
        [
            new StreamDataSkippedRow(_rtId, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), null,
                ArchiveRowOrigin.Manual, StreamDataSkipReason.LowerPrecedence)
        ]);
        A.CallTo(() => _repository.InsertTimeRangeAsync(A<OctoObjectId>._, A<IEnumerable<TimeRangeStreamDataPoint>>._, A<CancellationToken>._))
            .ThrowsAsync(new ManualStreamDataNotAppliedException(_archiveRtId, writeResult));

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(OnePoint(", \"origin\": \"manual\"")));

        result.Should().BeOfType<ConflictObjectResult>();
    }

    private static IReadOnlyList<InsertTimeRangePointRestDto> Bind(string body) =>
        JsonSerializer.Deserialize<IReadOnlyList<InsertTimeRangePointRestDto>>(body, MvcBodyOptions)
        ?? throw new InvalidOperationException("The body deserialized to null.");

    private (OctoObjectId ArchiveRtId, IReadOnlyList<TimeRangeStreamDataPoint> Points) CapturedInsert()
    {
        var call = Fake.GetCalls(_repository)
            .Should().ContainSingle(c => c.Method.Name == nameof(IStreamDataRepository.InsertTimeRangeAsync))
            .Subject;
        var points = call.GetArgument<IEnumerable<TimeRangeStreamDataPoint>>(1)
            ?? throw new InvalidOperationException("No points were passed to the repository.");
        return (call.GetArgument<OctoObjectId>(0), points.ToList());
    }
}
