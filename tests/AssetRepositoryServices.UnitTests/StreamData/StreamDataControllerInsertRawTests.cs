using System.Security.Claims;
using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AssetRepositoryServices.UnitTests.StreamData;

/// <summary>
///     AB#8590: <c>POST archives/{archiveRtId}/insert</c> writes and corrects raw-archive rows through the existing
///     <see cref="IStreamDataRepository.InsertAsync(OctoObjectId, IEnumerable{StreamDataPoint})" />. The body is bound
///     with the MVC defaults, the repository and the archive store are faked; the tests assert what reaches the
///     repository and what the caller is answered.
/// </summary>
public class StreamDataControllerInsertRawTests
{
    private const string TenantId = "maco";
    private const string CkTypeId = "Basic.Energy/EnergyMeasurement";

    private static readonly JsonSerializerOptions MvcBodyOptions = new JsonOptions().JsonSerializerOptions;

    private readonly OctoObjectId _archiveRtId = OctoObjectId.GenerateNewId();
    private readonly OctoObjectId _rtId = OctoObjectId.GenerateNewId();
    private readonly IStreamDataRepository _repository = A.Fake<IStreamDataRepository>();
    private readonly IArchiveRuntimeStore _archiveStore = A.Fake<IArchiveRuntimeStore>();
    private readonly StreamDataController _controller;

    public StreamDataControllerInsertRawTests()
    {
        var tenantContext = A.Fake<ITenantContext>();
        A.CallTo(() => tenantContext.GetStreamDataRepository()).Returns(_repository);
        A.CallTo(() => tenantContext.GetArchiveRuntimeStore()).Returns(_archiveStore);
        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.FindTenantContextAsync(TenantId)).Returns(tenantContext);
        A.CallTo(() => _repository.InsertAsync(A<OctoObjectId>._, A<IEnumerable<StreamDataPoint>>._))
            .ReturnsLazily(call => Task.FromResult(new StreamDataWriteResult(call.GetArgument<IEnumerable<StreamDataPoint>>(1)!.Count(), [])));

        _controller = new StreamDataController(
            A.Fake<ILogger<StreamDataController>>(),
            systemContext,
            A.Fake<IHostApplicationLifetime>());
        SignInAs(new Claim("sub", "user-1"), new Claim("preferred_username", "jane.doe"));

        // Raw archive with two ingested columns and one computed column.
        GivenArchive(new ArchiveSnapshot(_archiveRtId, new RtCkId<CkTypeId>(CkTypeId), CkArchiveStatus.Activated, "raw",
        [
            new CkArchiveColumnSpec("Amount.Value", false, false),
            new CkArchiveColumnSpec("Amount.Unit", false, false),
            new CkArchiveColumnSpec(string.Empty, false, false) { Name = "Doubled", Formula = "amountvalue * 2" }
        ]));
    }

    // ---- write and map ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Insert_HandsTheRawPointsToTheRepository()
    {
        var result = await Insert($$"""
            [{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}", "timestamp": "2026-03-01T10:15:00.123Z",
               "rtWellKnownName": "meter-1", "attributes": { "Amount.Value": 10.5, "Amount.Unit": "kWh" } }]
            """);

        result.Should().BeOfType<OkObjectResult>();
        var (archiveRtId, points) = CapturedInsert();
        archiveRtId.Should().Be(_archiveRtId);
        var point = points.Should().ContainSingle().Subject;
        point.RtId.Should().Be(_rtId);
        point.CkTypeId.Should().Be(new RtCkId<CkTypeId>(CkTypeId));
        point.Timestamp.Should().Be(new DateTime(2026, 3, 1, 10, 15, 0, 123, DateTimeKind.Utc));
        point.Timestamp.Kind.Should().Be(DateTimeKind.Utc);
        point.RtWellKnownName.Should().Be("meter-1");
        point.Attributes["Amount.Value"].Should().BeOfType<double>().Which.Should().Be(10.5);
        point.Attributes["Amount.Unit"].Should().Be("kWh");
        point.Origin.Should().Be(ArchiveRowOrigin.Automated);
        point.ManualWrittenBy.Should().BeNull();
        point.ManualReason.Should().BeNull();
    }

    [Fact]
    public async Task Insert_AnAutomatedPoint_MayNameOnlySomeColumns()
    {
        var result = await Insert(OnePoint("""{ "Amount.Value": 1.5 }""", ""));

        result.Should().BeOfType<OkObjectResult>();
        CapturedInsert().Points.Single().Attributes.Should().ContainSingle();
    }

    [Fact]
    public async Task Insert_ATimestampWithOffset_IsConvertedToUtc_AndOneWithoutOffsetIsReadAsUtc()
    {
        await Insert($$"""
            [{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}", "timestamp": "2026-03-01T11:15:00+01:00" },
             { "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}", "timestamp": "2026-03-01T10:30:00" }]
            """);

        var points = CapturedInsert().Points;
        points[0].Timestamp.Should().Be(new DateTime(2026, 3, 1, 10, 15, 0, DateTimeKind.Utc));
        points[0].Timestamp.Kind.Should().Be(DateTimeKind.Utc);
        points[1].Timestamp.Should().Be(new DateTime(2026, 3, 1, 10, 30, 0, DateTimeKind.Utc));
        points[1].Timestamp.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Insert_AnEmptyBatch_Answers204_AndWritesNothing()
    {
        var result = await Insert("[]");

        result.Should().BeOfType<NoContentResult>();
        NothingWritten();
    }

    [Fact]
    public void Insert_RequiresTheReadWritePolicy()
    {
        var policies = typeof(StreamDataController).GetMethod(nameof(StreamDataController.Insert))!
            .GetCustomAttributes(typeof(AuthorizeAttribute), false).Cast<AuthorizeAttribute>().Select(a => a.Policy);

        policies.Should().Contain(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy);
    }

    [Fact]
    public void Insert_IsRoutedNextToInsertTimeRange()
    {
        var route = typeof(StreamDataController).GetMethod(nameof(StreamDataController.Insert))!
            .GetCustomAttributes(typeof(HttpPostAttribute), false).Cast<HttpPostAttribute>().Single();

        route.Template.Should().Be("archives/{archiveRtId}/insert");
    }

    // ---- origin, reason, writer -------------------------------------------------------------------------------

    [Theory]
    [InlineData("manual")]
    [InlineData("MANUAL")]
    public async Task Insert_AManualPoint_RecordsTheCallerAsWriterAndTheReasonAsGiven(string origin)
    {
        var result = await Insert(OnePoint(FullRow, $", \"origin\": \"{origin}\", \"reason\": \"meter exchanged\""));

        result.Should().BeOfType<OkObjectResult>();
        var point = CapturedInsert().Points.Single();
        point.Origin.Should().Be(ArchiveRowOrigin.Manual);
        point.ManualWrittenBy.Should().Be("jane.doe");
        point.ManualReason.Should().Be("meter exchanged");
    }

    [Fact]
    public async Task Insert_AWriterInTheBody_IsNeverRecorded()
    {
        await Insert(OnePoint(FullRow, ", \"origin\": \"manual\", \"manualWrittenBy\": \"mallory\", \"manualWrittenAt\": \"2020-01-01T00:00:00Z\""));

        CapturedInsert().Points.Single().ManualWrittenBy.Should().Be("jane.doe");
    }

    [Fact]
    public async Task Insert_AClientCredentialsToken_IsRecordedByItsClientId()
    {
        SignInAs(new Claim("client_id", "import-service"));

        await Insert(OnePoint(FullRow, ", \"origin\": \"manual\""));

        CapturedInsert().Points.Single().ManualWrittenBy.Should().Be("import-service");
    }

    [Theory]
    [InlineData("Manuel")]
    [InlineData("1")]
    public async Task Insert_AnInvalidOrigin_Answers400NamingPointAndValue(string origin)
    {
        var result = await Insert(OnePoint(FullRow, $", \"origin\": \"{origin}\""));

        BadRequestMessage(result).Should().Contain("Point 0").And.Contain(origin).And.Contain("'manual' or 'automated'");
        NothingWritten();
    }

    [Fact]
    public async Task Insert_AReasonWithoutManual_Answers400()
    {
        var result = await Insert(OnePoint(FullRow, ", \"reason\": \"why\""));

        BadRequestMessage(result).Should().Contain("Point 0").And.Contain("reason");
        NothingWritten();
    }

    [Fact]
    public async Task Insert_AManualPointWithATokenWithoutWriter_Answers400()
    {
        SignInAs();

        var result = await Insert(OnePoint(FullRow, ", \"origin\": \"manual\""));

        BadRequestMessage(result).Should().Contain("names no subject or client");
        NothingWritten();
    }

    // ---- D1: a manual point names every ingested column ------------------------------------------------------

    [Fact]
    public async Task Insert_AManualPointMissingAColumn_Answers400ListingTheMissingColumns()
    {
        var result = await Insert(OnePoint("""{ "Amount.Value": 1.5 }""", ", \"origin\": \"manual\""));

        var message = BadRequestMessage(result);
        message.Should().Contain("Point 0").And.Contain("'Amount.Unit'").And.NotContain("'Amount.Value'");
        message.Should().NotContain("Doubled", "a computed column is never named by the caller");
        NothingWritten();
    }

    [Fact]
    public async Task Insert_AManualPointWithExplicitNulls_IsAccepted()
    {
        var result = await Insert(OnePoint("""{ "Amount.Value": 1.5, "Amount.Unit": null }""", ", \"origin\": \"manual\""));

        result.Should().BeOfType<OkObjectResult>();
        CapturedInsert().Points.Single().Attributes.Should().ContainKey("Amount.Unit").WhoseValue.Should().BeNull();
    }

    [Fact]
    public async Task Insert_ColumnsAreMatchedLikeTheStorageColumn_IgnoringCase()
    {
        var result = await Insert(OnePoint("""{ "amount.value": 1.5, "AMOUNT.UNIT": "kWh" }""", ", \"origin\": \"manual\""));

        result.Should().BeOfType<OkObjectResult>();
    }

    // ---- unknown and computed columns, ckTypeId, identity --------------------------------------------------

    [Fact]
    public async Task Insert_AnUnknownColumn_Answers400NamingPointAndAttribute()
    {
        var result = await Insert($$"""
            [{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}", "timestamp": "2026-03-01T10:00:00Z", "attributes": { "Amount.Value": 1 } },
             { "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}", "timestamp": "2026-03-01T10:15:00Z", "attributes": { "Voltage": 230 } }]
            """);

        BadRequestMessage(result).Should().Contain("Point 1").And.Contain("'Voltage'").And.Contain("'Amount.Value'");
        NothingWritten();
    }

    [Fact]
    public async Task Insert_AComputedColumn_Answers400()
    {
        var result = await Insert(OnePoint("""{ "Amount.Value": 1, "doubled": 2 }""", ""));

        BadRequestMessage(result).Should().Contain("Point 0").And.Contain("computed column 'Doubled'");
        NothingWritten();
    }

    [Fact]
    public async Task Insert_TwoKeysForTheSameColumn_Answer400()
    {
        var result = await Insert(OnePoint("""{ "Amount.Value": 1, "amountvalue": 2 }""", ""));

        BadRequestMessage(result).Should().Contain("address the same column");
        NothingWritten();
    }

    [Fact]
    public async Task Insert_ACkTypeIdMismatch_Answers400NamingBothSides()
    {
        var result = await Insert($$"""
            [{ "rtId": "{{_rtId}}", "ckTypeId": "Basic.Energy-1.0.0/EnergyMeasurement-1", "timestamp": "2026-03-01T10:00:00Z" }]
            """);

        BadRequestMessage(result).Should().Contain(CkTypeId).And.Contain("Basic.Energy-1.0.0/EnergyMeasurement-1");
        NothingWritten();
    }

    [Fact]
    public async Task Insert_ASubMillisecondTimestamp_Answers400()
    {
        var result = await Insert($$"""
            [{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}", "timestamp": "2026-03-01T10:00:00.1234Z" }]
            """);

        BadRequestMessage(result).Should().Contain("Point 0").And.Contain("sub-millisecond");
        NothingWritten();
    }

    [Fact]
    public async Task Insert_AMissingTimestamp_Answers400()
    {
        var result = await Insert($$"""[{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}" }]""");

        BadRequestMessage(result).Should().Contain("Point 0").And.Contain("timestamp");
        NothingWritten();
    }

    [Fact]
    public async Task Insert_AnInvalidRtId_Answers400()
    {
        var result = await Insert($$"""[{ "rtId": "not-an-id", "ckTypeId": "{{CkTypeId}}", "timestamp": "2026-03-01T10:00:00Z" }]""");

        BadRequestMessage(result).Should().Contain("Point 0").And.Contain("not-an-id");
        NothingWritten();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Insert_IntoATimeRangeOrRollupArchive_Answers400(bool timeRange)
    {
        var snapshot = new ArchiveSnapshot(_archiveRtId, new RtCkId<CkTypeId>(CkTypeId), CkArchiveStatus.Activated, "x", []);
        GivenArchive(timeRange ? snapshot with { IsTimeRange = true } : snapshot with { RollupAggregations = [] });

        var result = await Insert(OnePoint("{}", ""));

        BadRequestMessage(result).Should().Contain(timeRange ? "insertTimeRange" : "rollup archive");
        NothingWritten();
    }

    [Fact]
    public async Task Insert_WithoutASnapshot_LeavesTheArchiveErrorToTheRepository()
    {
        GivenArchive(null);
        A.CallTo(() => _repository.InsertAsync(A<OctoObjectId>._, A<IEnumerable<StreamDataPoint>>._))
            .ThrowsAsync(new ArchiveNotFoundException(_archiveRtId));

        var result = await Insert(OnePoint(FullRow, ""));

        BadRequestMessage(result).Should().Contain(_archiveRtId.ToString());
    }

    [Fact]
    public async Task Insert_AValueThatDoesNotFitItsColumn_Answers400WithTheValidatorMessage()
    {
        // AB#8606: the repository's value check throws an ArgumentException naming point and column, like the
        // time-range path (ArchiveWriteValueValidator).
        A.CallTo(() => _repository.InsertAsync(A<OctoObjectId>._, A<IEnumerable<StreamDataPoint>>._))
            .ThrowsAsync(new ArgumentException("Point 0: value 'abc' of column 'Amount.Value' is not a Double."));

        var result = await Insert(OnePoint("""{ "Amount.Value": "abc" }""", ""));

        BadRequestMessage(result).Should().Contain("Point 0").And.Contain("Amount.Value");
    }

    [Fact]
    public async Task Insert_StreamDataDisabled_Answers400()
    {
        var tenantContext = A.Fake<ITenantContext>();
        A.CallTo(() => tenantContext.GetStreamDataRepository()).Returns(null);
        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.FindTenantContextAsync(TenantId)).Returns(tenantContext);
        var controller = new StreamDataController(A.Fake<ILogger<StreamDataController>>(), systemContext,
            A.Fake<IHostApplicationLifetime>());

        var result = await controller.Insert(TenantId, _archiveRtId.ToString(), Bind(OnePoint(FullRow, "")));

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ---- write result ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Insert_AnswersWithTheWriteResult_ASkippedRawRowHasTimestampAsFromAndNoTo()
    {
        var timestamp = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        A.CallTo(() => _repository.InsertAsync(A<OctoObjectId>._, A<IEnumerable<StreamDataPoint>>._))
            .Returns(new StreamDataWriteResult(0,
                [new StreamDataSkippedRow(_rtId, timestamp, null, ArchiveRowOrigin.Automated, StreamDataSkipReason.ManualValueProtected)]));

        var result = await Insert(OnePoint(FullRow, ""));

        var body = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<StreamDataWriteResultRestDto>().Subject;
        body.Stored.Should().Be(0);
        body.Skipped.Should().Be(1);
        body.SkippedByReason.Should().ContainKey("ManualValueProtected");
        var row = body.SkippedRows.Should().ContainSingle().Subject;
        row.From.Should().Be(timestamp);
        row.To.Should().BeNull();
        row.Origin.Should().Be("automated");
    }

    [Fact]
    public async Task Insert_AManualRowNotStored_Answers409WithTheResult()
    {
        var writeResult = new StreamDataWriteResult(0,
            [new StreamDataSkippedRow(_rtId, new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), null,
                ArchiveRowOrigin.Manual, StreamDataSkipReason.LowerPrecedence)]);
        A.CallTo(() => _repository.InsertAsync(A<OctoObjectId>._, A<IEnumerable<StreamDataPoint>>._))
            .ThrowsAsync(new ManualStreamDataNotAppliedException(_archiveRtId, writeResult));

        var result = await Insert(OnePoint(FullRow, ", \"origin\": \"manual\""));

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject.Value.Should().BeOfType<ManualWriteNotStoredRestDto>().Subject;
        conflict.Message.Should().NotBeNullOrEmpty();
        conflict.Result.Stored.Should().Be(0);
        conflict.Result.SkippedRows.Should().ContainSingle().Which.Origin.Should().Be("manual");
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private const string FullRow = """{ "Amount.Value": 1.5, "Amount.Unit": "kWh" }""";

    private string OnePoint(string attributes, string extra) => $$"""
        [{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}", "timestamp": "2026-03-01T10:00:00Z",
           "attributes": {{attributes}} {{extra}} }]
        """;

    private void GivenArchive(ArchiveSnapshot? snapshot) =>
        A.CallTo(() => _archiveStore.GetAsync(_archiveRtId)).Returns(snapshot);

    private void SignInAs(params Claim[] claims) =>
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) }
        };

    private Task<IActionResult> Insert(string body) => _controller.Insert(TenantId, _archiveRtId.ToString(), Bind(body));

    private static IReadOnlyList<InsertRawPointRestDto> Bind(string body) =>
        JsonSerializer.Deserialize<IReadOnlyList<InsertRawPointRestDto>>(body, MvcBodyOptions)
        ?? throw new InvalidOperationException("The body deserialized to null.");

    private static string BadRequestMessage(IActionResult result) =>
        result.Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString()!;

    private void NothingWritten() =>
        Fake.GetCalls(_repository).Should().NotContain(c => c.Method.Name == nameof(IStreamDataRepository.InsertAsync));

    private (OctoObjectId ArchiveRtId, IReadOnlyList<StreamDataPoint> Points) CapturedInsert()
    {
        var call = Fake.GetCalls(_repository)
            .Should().ContainSingle(c => c.Method.Name == nameof(IStreamDataRepository.InsertAsync)
                                         && c.Arguments.Count == 2 && c.Arguments[1] is IEnumerable<StreamDataPoint>)
            .Subject;
        var points = call.GetArgument<IEnumerable<StreamDataPoint>>(1)
            ?? throw new InvalidOperationException("No points were passed to the repository.");
        return (call.GetArgument<OctoObjectId>(0), points.ToList());
    }
}
