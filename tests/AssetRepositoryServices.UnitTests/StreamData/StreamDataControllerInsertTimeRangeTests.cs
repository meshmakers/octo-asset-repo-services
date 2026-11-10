using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
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
    private readonly IArchiveRuntimeStore _archiveStore = A.Fake<IArchiveRuntimeStore>();
    private readonly StreamDataController _controller;

    public StreamDataControllerInsertTimeRangeTests()
    {
        var tenantContext = A.Fake<ITenantContext>();
        A.CallTo(() => tenantContext.GetStreamDataRepository()).Returns(_repository);
        A.CallTo(() => tenantContext.GetArchiveRuntimeStore()).Returns(_archiveStore);
        // No archive snapshot by default: binding, origin and role tests do not depend on the archive's columns, and
        // without a snapshot the column checks are skipped (the repository answers itself). The column tests declare
        // the archive with GivenTimeRangeArchiveWithTwoColumns.
        A.CallTo(() => _archiveStore.GetAsync(_archiveRtId)).Returns((ArchiveSnapshot?)null);
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

    private static readonly System.Security.Claims.Claim StreamDataAdmin = new("role", CommonConstants.StreamDataAdminRole);

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
        SignInAs(new System.Security.Claims.Claim("sub", "user-1"), StreamDataAdmin);

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
            new System.Security.Claims.Claim("name", "Jane Doe"), StreamDataAdmin);

        await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(OnePoint(", \"origin\": \"manual\"")));

        CapturedInsert().Points.Single().ManualWrittenBy.Should().Be("jane.doe");
    }

    [Fact]
    public async Task InsertTimeRange_ManualOrigin_WithoutAPreferredUsername_RecordsTheName()
    {
        SignInAs(new System.Security.Claims.Claim("sub", "8a4c1f00-0000-4000-8000-000000000001"),
            new System.Security.Claims.Claim("name", "Jane Doe"), StreamDataAdmin);

        await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(OnePoint(", \"origin\": \"manual\"")));

        CapturedInsert().Points.Single().ManualWrittenBy.Should().Be("Jane Doe");
    }

    [Fact]
    public async Task InsertTimeRange_ManualOrigin_ANameInTheBodyIsNeverTheWriter()
    {
        SignInAs(new System.Security.Claims.Claim("sub", "user-1"), new System.Security.Claims.Claim("preferred_username", "jane.doe"), StreamDataAdmin);

        await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(),
            Bind(OnePoint(", \"origin\": \"manual\", \"manualWrittenBy\": \"mallory\", \"writer\": \"mallory\"")));

        CapturedInsert().Points.Single().ManualWrittenBy.Should().Be("jane.doe");
    }

    [Fact]
    public async Task InsertTimeRange_ManualOriginWithoutReason_IsAccepted()
    {
        SignInAs(new System.Security.Claims.Claim("sub", "user-1"), StreamDataAdmin);

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(OnePoint(", \"origin\": \"manual\"")));

        result.Should().BeOfType<OkObjectResult>();
        CapturedInsert().Points.Single().ManualReason.Should().BeNull();
    }

    [Fact]
    public async Task InsertTimeRange_AClientCredentialsToken_IsRecordedByItsClientId()
    {
        SignInAs(new System.Security.Claims.Claim("client_id", "import-service"), StreamDataAdmin);

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
        SignInAs(StreamDataAdmin);

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

    // ---- AB#8590 AC 8: a manual point needs the tenant role StreamDataAdmin -----------------------------------

    [Fact]
    public async Task InsertTimeRange_AManualPointWithoutTheStreamDataAdminRole_Answers403_AndWritesNothing()
    {
        SignInAs(new System.Security.Claims.Claim("sub", "user-1"), new System.Security.Claims.Claim("scope", CommonConstants.OctoApiFullAccess));

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(OnePoint(", \"origin\": \"manual\"")));

        var forbidden = result.Should().BeOfType<ObjectResult>().Subject;
        forbidden.StatusCode.Should().Be(403);
        forbidden.Value.Should().BeOfType<ForbiddenErrorDto>().Which.Message.Should().Contain(CommonConstants.StreamDataAdminRole);
        Fake.GetCalls(_repository).Should().NotContain(c => c.Method.Name == nameof(IStreamDataRepository.InsertTimeRangeAsync));
    }

    [Fact]
    public async Task InsertTimeRange_AMixedBatchWithoutTheStreamDataAdminRole_IsRefusedAsAWhole()
    {
        SignInAs(new System.Security.Claims.Claim("sub", "user-1"), new System.Security.Claims.Claim("scope", CommonConstants.OctoApiFullAccess));
        var body = $$"""
            [{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}", "from": "2026-08-31T22:00:00Z", "to": "2026-09-01T22:00:00Z" },
             { "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}", "from": "2026-09-01T22:00:00Z", "to": "2026-09-02T22:00:00Z",
               "origin": "manual" }]
            """;

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(body));

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        Fake.GetCalls(_repository).Should().NotContain(c => c.Method.Name == nameof(IStreamDataRepository.InsertTimeRangeAsync));
    }

    [Fact]
    public async Task InsertTimeRange_AnAutomatedPointWithoutTheStreamDataAdminRole_IsWritten()
    {
        SignInAs(new System.Security.Claims.Claim("sub", "user-1"), new System.Security.Claims.Claim("scope", CommonConstants.OctoApiFullAccess));

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(OnePoint(", \"origin\": \"automated\"")));

        result.Should().BeOfType<OkObjectResult>();
        CapturedInsert().Points.Single().Origin.Should().Be(ArchiveRowOrigin.Automated);
    }

    [Fact]
    public async Task InsertTimeRange_AManualPointWithTheStreamDataAdminRole_IsWritten()
    {
        SignInAs(new System.Security.Claims.Claim("sub", "user-1"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, CommonConstants.StreamDataAdminRole));

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(OnePoint(", \"origin\": \"manual\"")));

        result.Should().BeOfType<OkObjectResult>();
        CapturedInsert().Points.Single().Origin.Should().Be(ArchiveRowOrigin.Manual);
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
        SignInAs(new System.Security.Claims.Claim("sub", "user-1"), StreamDataAdmin);
        var writeResult = new StreamDataWriteResult(0,
        [
            new StreamDataSkippedRow(_rtId, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), null,
                ArchiveRowOrigin.Manual, StreamDataSkipReason.LowerPrecedence)
        ]);
        A.CallTo(() => _repository.InsertTimeRangeAsync(A<OctoObjectId>._, A<IEnumerable<TimeRangeStreamDataPoint>>._, A<CancellationToken>._))
            .ThrowsAsync(new ManualStreamDataNotAppliedException(_archiveRtId, writeResult));

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(OnePoint(", \"origin\": \"manual\"")));

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;

        // The SDK and the energy pipelines read this body: the property names are the wire contract.
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(conflict.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        body.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("message", "result");
        body.RootElement.GetProperty("message").GetString().Should().NotBeNullOrEmpty();
        var writeResultBody = body.RootElement.GetProperty("result");
        writeResultBody.GetProperty("stored").GetInt32().Should().Be(0);
        writeResultBody.GetProperty("skipped").GetInt32().Should().Be(1);
        writeResultBody.GetProperty("skippedByReason").GetProperty("LowerPrecedence").GetInt32().Should().Be(1);
        writeResultBody.GetProperty("skippedRows")[0].GetProperty("origin").GetString().Should().Be("manual");
    }

    // ---- AB#8590 AC 9: a manual point names every ingested column -------------------------------------------

    /// <summary>
    /// A time-range archive with two ingested columns and one computed column. A manual point protects the whole row,
    /// so it must name both ingested columns (AB#8590 AC 9).
    /// </summary>
    private void GivenTimeRangeArchiveWithTwoColumns() =>
        A.CallTo(() => _archiveStore.GetAsync(_archiveRtId)).Returns(
            new ArchiveSnapshot(_archiveRtId, new RtCkId<CkTypeId>(CkTypeId), CkArchiveStatus.Activated, "timeRange",
            [
                new CkArchiveColumnSpec("Amount.Value", false, false),
                new CkArchiveColumnSpec("Amount.Unit", false, false),
                new CkArchiveColumnSpec(string.Empty, false, false) { Name = "Doubled", Formula = "amountvalue * 2" }
            ]) { IsTimeRange = true });

    [Fact]
    public async Task InsertTimeRange_AManualPointMissingAColumn_Answers400NamingTheColumn_AndWritesNothing()
    {
        GivenTimeRangeArchiveWithTwoColumns();
        SignInAs(new System.Security.Claims.Claim("sub", "user-1"), StreamDataAdmin);

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(OnePoint(", \"origin\": \"manual\"")));

        var message = result.Should().BeOfType<BadRequestObjectResult>().Subject.Value.Should().BeOfType<string>().Subject;
        message.Should().Contain("Point 0").And.Contain("'Amount.Unit'").And.NotContain("'Amount.Value'");
        message.Should().NotContain("Doubled", "a computed column is never named by the caller");
        Fake.GetCalls(_repository).Should().NotContain(c => c.Method.Name == nameof(IStreamDataRepository.InsertTimeRangeAsync));
    }

    [Fact]
    public async Task InsertTimeRange_AManualPointWithEveryColumn_OneExplicitlyNull_IsWrittenAsSent()
    {
        GivenTimeRangeArchiveWithTwoColumns();
        SignInAs(new System.Security.Claims.Claim("sub", "user-1"), StreamDataAdmin);
        var body = $$"""
            [{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}",
               "from": "2026-08-31T22:00:00Z", "to": "2026-09-01T22:00:00Z",
               "attributes": { "amount.value": 1.5, "Amount.Unit": null }, "origin": "manual" }]
            """;

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(body));

        result.Should().BeOfType<OkObjectResult>();
        var attributes = CapturedInsert().Points.Single().Attributes;
        attributes.Should().HaveCount(2);
        attributes["amount.value"].Should().Be(1.5);
        attributes.Should().ContainKey("Amount.Unit").WhoseValue.Should().BeNull();
    }

    // engine-mongodb AB#8622: a ranking column of the conflict precedence cannot be null.

    private void GivenRankedTimeRangeArchive() =>
        A.CallTo(() => _archiveStore.GetAsync(_archiveRtId)).Returns(
            new ArchiveSnapshot(_archiveRtId, new RtCkId<CkTypeId>(CkTypeId), CkArchiveStatus.Activated, "timeRange",
            [
                new CkArchiveColumnSpec("Amount.Value", false, false),
                new CkArchiveColumnSpec("DataQuality", false, false),
                new CkArchiveColumnSpec("SourceDocumentDate", false, false)
            ])
            {
                IsTimeRange = true,
                ConflictPrecedence =
                [
                    new ArchiveConflictKey("DataQuality", ConflictKeyOrder.LowerWins),
                    new ArchiveConflictKey(ArchiveConflictKey.OriginKey, ConflictKeyOrder.HigherWins),
                    new ArchiveConflictKey("SourceDocumentDate", ConflictKeyOrder.HigherWins)
                ]
            });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InsertTimeRange_AnExplicitNullOnARankingColumn_Answers400NamingPointAndField_AndWritesNothing(bool manual)
    {
        GivenRankedTimeRangeArchive();
        SignInAs(new System.Security.Claims.Claim("sub", "user-1"), StreamDataAdmin);
        var body = $$"""
            [{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}",
               "from": "2026-08-31T22:00:00Z", "to": "2026-09-01T22:00:00Z",
               "attributes": { "Amount.Value": 1.5, "DataQuality": 2, "SourceDocumentDate": null }
               {{(manual ? ", \"origin\": \"manual\"" : "")}} }]
            """;

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(body));

        var message = BadRequestMessage(result);
        message.Should().Contain("Point 0").And.Contain("'SourceDocumentDate'").And.Contain("cannot be null")
            .And.NotContain("$Origin");
        RepositoryWasNotCalled();
    }

    [Fact]
    public async Task InsertTimeRange_AnAutomatedPointWithoutItsRankingColumns_IsPassedOn()
    {
        GivenRankedTimeRangeArchive();

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(OnePoint("")));

        result.Should().BeOfType<OkObjectResult>();
        CapturedInsert().Points.Single().Attributes.Should().ContainSingle().Which.Key.Should().Be("Amount.Value");
    }

    [Fact]
    public async Task InsertTimeRange_AManualPointWithoutItsRankingColumns_IsAccepted()
    {
        GivenRankedTimeRangeArchive();
        SignInAs(new System.Security.Claims.Claim("sub", "user-1"), StreamDataAdmin);

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(OnePoint(", \"origin\": \"manual\"")));

        result.Should().BeOfType<OkObjectResult>();
        var point = CapturedInsert().Points.Single();
        point.Origin.Should().Be(ArchiveRowOrigin.Manual);
        point.Attributes.Should().ContainSingle().Which.Key.Should().Be("Amount.Value",
            "a ranking column the point leaves out is not sent, so it keeps its stored value");
    }

    [Fact]
    public async Task InsertTimeRange_AManualPointWithoutANormalColumn_StillAnswers400NamingIt()
    {
        GivenRankedTimeRangeArchive();
        SignInAs(new System.Security.Claims.Claim("sub", "user-1"), StreamDataAdmin);
        var body = $$"""
            [{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}",
               "from": "2026-08-31T22:00:00Z", "to": "2026-09-01T22:00:00Z",
               "attributes": { "DataQuality": 2 }, "origin": "manual" }]
            """;

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(body));

        var message = BadRequestMessage(result);
        message.Should().Contain("Point 0").And.Contain("Missing: 'Amount.Value'.");
        message.Should().NotContain("'SourceDocumentDate'", "a ranking column may be left out");
        RepositoryWasNotCalled();
    }

    [Fact]
    public async Task InsertTimeRange_AnAutomatedPointMissingAColumn_IsPassedOnUnchanged()
    {
        GivenTimeRangeArchiveWithTwoColumns();

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(OnePoint("")));

        result.Should().BeOfType<OkObjectResult>();
        CapturedInsert().Points.Single().Attributes.Should().ContainSingle().Which.Key.Should().Be("Amount.Value");
    }

    [Fact]
    public async Task InsertTimeRange_AManualPointMissingAColumn_IsRefusedAsAWholeBatch()
    {
        GivenTimeRangeArchiveWithTwoColumns();
        SignInAs(new System.Security.Claims.Claim("sub", "user-1"), StreamDataAdmin);
        var body = $$"""
            [{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}",
               "from": "2026-08-31T22:00:00Z", "to": "2026-09-01T22:00:00Z",
               "attributes": { "Amount.Value": 1.5, "Amount.Unit": "kWh" }, "origin": "manual" },
             { "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}",
               "from": "2026-09-01T22:00:00Z", "to": "2026-09-02T22:00:00Z",
               "attributes": { "Amount.Unit": "kWh" }, "origin": "manual" }]
            """;

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(body));

        result.Should().BeOfType<BadRequestObjectResult>().Subject.Value.Should().BeOfType<string>()
            .Which.Should().Contain("Point 1").And.Contain("'Amount.Value'");
        Fake.GetCalls(_repository).Should().NotContain(c => c.Method.Name == nameof(IStreamDataRepository.InsertTimeRangeAsync));
    }

    // ---- AB#8590 point 11: the same column and precision rules as the raw insert ---------------------------------

    /// <summary>One point of the two-column archive, with every ingested column and the given extra JSON.</summary>
    private string ParityPoint(bool manual, string extraAttributes = "", string from = "2026-08-31T22:00:00Z",
        string to = "2026-09-01T22:00:00Z") => $$"""
        [{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}", "from": "{{from}}", "to": "{{to}}",
           "attributes": { "Amount.Value": 1.5, "Amount.Unit": "kWh" {{extraAttributes}} }
           {{(manual ? ", \"origin\": \"manual\"" : "")}} }]
        """;

    private void SignInForParity(bool manual)
    {
        GivenTimeRangeArchiveWithTwoColumns();
        if (manual)
        {
            SignInAs(new System.Security.Claims.Claim("sub", "user-1"), StreamDataAdmin);
        }
        else
        {
            SignInAs(new System.Security.Claims.Claim("sub", "user-1"));
        }
    }

    private void RepositoryWasNotCalled() =>
        Fake.GetCalls(_repository).Should().NotContain(c => c.Method.Name == nameof(IStreamDataRepository.InsertTimeRangeAsync));

    private static string BadRequestMessage(IActionResult result) =>
        result.Should().BeOfType<BadRequestObjectResult>().Subject.Value.Should().BeOfType<string>().Subject;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InsertTimeRange_AnUnknownColumn_Answers400NamingPointAndKey_AndWritesNothing(bool manual)
    {
        SignInForParity(manual);

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(),
            Bind(ParityPoint(manual, ", \"Amount.Typo\": 2")));

        BadRequestMessage(result).Should().Contain("Point 0").And.Contain("'Amount.Typo'").And.Contain("is not a column");
        RepositoryWasNotCalled();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InsertTimeRange_AComputedColumn_Answers400NamingPointAndColumn_AndWritesNothing(bool manual)
    {
        SignInForParity(manual);

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(),
            Bind(ParityPoint(manual, ", \"doubled\": 3.0")));

        BadRequestMessage(result).Should().Contain("Point 0").And.Contain("'doubled'").And.Contain("computed column 'Doubled'");
        RepositoryWasNotCalled();
    }

    [Theory]
    [InlineData(false, "from")]
    [InlineData(true, "from")]
    [InlineData(false, "to")]
    [InlineData(true, "to")]
    public async Task InsertTimeRange_ASubMillisecondWindowBound_Answers400NamingPointAndField_AndWritesNothing(bool manual,
        string field)
    {
        SignInForParity(manual);
        var body = field == "from"
            ? ParityPoint(manual, from: "2026-08-31T22:00:00.0005Z")
            : ParityPoint(manual, to: "2026-09-01T22:00:00.0000001Z");

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(body));

        BadRequestMessage(result).Should().Contain("Point 0").And.Contain($"{field} '").And.Contain("sub-millisecond");
        RepositoryWasNotCalled();
    }

    [Fact]
    public async Task InsertTimeRange_AMissingWindowStart_Answers400NamingTheField()
    {
        SignInForParity(manual: false);
        var body = $$"""
            [{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}", "to": "2026-09-01T22:00:00Z",
               "attributes": { "Amount.Value": 1.5, "Amount.Unit": "kWh" } }]
            """;

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(body));

        BadRequestMessage(result).Should().Contain("Point 0: from is missing");
        RepositoryWasNotCalled();
    }

    [Fact]
    public async Task InsertTimeRange_AWindowWithAnOffset_IsHandedOnInUtc()
    {
        SignInForParity(manual: false);

        await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(),
            Bind(ParityPoint(false, from: "2026-09-01T00:00:00+02:00", to: "2026-09-02T00:00:00+02:00")));

        var point = CapturedInsert().Points.Single();
        point.From.Should().Be(new DateTime(2026, 8, 31, 22, 0, 0, DateTimeKind.Utc));
        point.From.Kind.Should().Be(DateTimeKind.Utc);
        point.To.Should().Be(new DateTime(2026, 9, 1, 22, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task InsertTimeRange_ASecondPointWithAnUnknownColumn_RefusesTheWholeBatch()
    {
        SignInForParity(manual: false);
        var body = $$"""
            [{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}", "from": "2026-08-31T22:00:00Z", "to": "2026-09-01T22:00:00Z",
               "attributes": { "Amount.Value": 1.5 } },
             { "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}", "from": "2026-09-01T22:00:00Z", "to": "2026-09-02T22:00:00Z",
               "attributes": { "Amount.Value": 1.5, "Extra": "x" } }]
            """;

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(body));

        BadRequestMessage(result).Should().Contain("Point 1").And.Contain("'Extra'");
        RepositoryWasNotCalled();
    }

    // ---- AB#8590: malformed request: invalid ids and null points answer 400, never 500 -------------------------

    [Theory]
    [InlineData("not-an-id")]
    [InlineData("")]
    public async Task InsertTimeRange_AnInvalidArchiveRtId_Answers400NamingTheField_AndReadsNothing(string archiveRtId)
    {
        SignInForParity(manual: false);

        var result = await _controller.InsertTimeRange(TenantId, archiveRtId, Bind(ParityPoint(manual: false)));

        BadRequestMessage(result).Should().Contain("archiveRtId").And.Contain("is not a valid runtime id");
        A.CallTo(() => _archiveStore.GetAsync(A<OctoObjectId>._)).MustNotHaveHappened();
        RepositoryWasNotCalled();
    }

    [Fact]
    public async Task InsertTimeRange_AnInvalidPointRtId_Answers400NamingPointAndField_AndWritesNothing()
    {
        SignInForParity(manual: false);

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind($$"""
            [{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}", "from": "2026-08-31T22:00:00Z", "to": "2026-09-01T22:00:00Z" },
             { "rtId": "no-id", "ckTypeId": "{{CkTypeId}}", "from": "2026-09-01T22:00:00Z", "to": "2026-09-02T22:00:00Z" }]
            """));

        BadRequestMessage(result).Should().Contain("Point 1").And.Contain("rtId 'no-id'").And.Contain("is not a valid runtime id");
        RepositoryWasNotCalled();
    }

    [Fact]
    public async Task InsertTimeRange_ANullPoint_Answers400NamingThePoint_AndWritesNothing()
    {
        SignInForParity(manual: false);

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind("[null]"));

        BadRequestMessage(result).Should().Be("Point 0 is null.");
        RepositoryWasNotCalled();
    }

    [Fact]
    public async Task InsertTimeRange_ANullPointAfterAValidOne_Answers400NamingItsIndex_AndWritesNothing()
    {
        SignInForParity(manual: false);

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind($$"""
            [{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}", "from": "2026-08-31T22:00:00Z", "to": "2026-09-01T22:00:00Z" },
             null]
            """));

        BadRequestMessage(result).Should().Be("Point 1 is null.");
        RepositoryWasNotCalled();
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
