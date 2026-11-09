using System.Reflection;
using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using StreamDataWriteIncompleteException = Meshmakers.Octo.Runtime.Engine.CrateDb.StreamDataWriteIncompleteException;

namespace AssetRepositoryServices.UnitTests.StreamData;

/// <summary>
///     AB#8679: when CrateDB does not store every row of an archive write (a memory circuit breaker ends a shard request
///     without an error) and the retry does not either, the repository throws
///     <see cref="StreamDataWriteIncompleteException" />. The raw <c>insert</c> and the <c>insertTimeRange</c> endpoints
///     answer 503 with the exception message (archive table and row counts, no stack trace), so the caller sends the
///     batch again. The other exceptions of the write keep their mapping.
/// </summary>
public class StreamDataControllerWriteIncompleteTests
{
    private const string TenantId = "maco";
    private const string CkTypeId = "Basic.Energy/EnergyMeasurement";
    private const string QualifiedTable = "maco.archive_6720a1b2c3d4e5f600000001";

    private static readonly JsonSerializerOptions MvcBodyOptions = new JsonOptions().JsonSerializerOptions;

    private readonly OctoObjectId _archiveRtId = OctoObjectId.GenerateNewId();
    private readonly OctoObjectId _rtId = OctoObjectId.GenerateNewId();
    private readonly IStreamDataRepository _repository = A.Fake<IStreamDataRepository>();
    private readonly StreamDataController _controller;

    public StreamDataControllerWriteIncompleteTests()
    {
        var archiveStore = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => archiveStore.GetAsync(_archiveRtId)).Returns((ArchiveSnapshot?)null);
        var tenantContext = A.Fake<ITenantContext>();
        A.CallTo(() => tenantContext.GetStreamDataRepository()).Returns(_repository);
        A.CallTo(() => tenantContext.GetArchiveRuntimeStore()).Returns(archiveStore);
        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.FindTenantContextAsync(TenantId)).Returns(tenantContext);

        _controller = new StreamDataController(
            A.Fake<ILogger<StreamDataController>>(),
            systemContext,
            A.Fake<IHostApplicationLifetime>());
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
    }

    // ---- 503 for an incomplete write ---------------------------------------------------------------------------

    [Fact]
    public async Task Insert_WriteIncomplete_Answers503WithArchiveAndRowCounts()
    {
        GivenInsertThrows(WriteIncomplete(rowsSent: 10, rowsMissing: 3));

        var result = await Insert();

        ServiceUnavailableMessage(result).Should().Contain("3 of 10 rows").And.Contain(QualifiedTable);
    }

    [Fact]
    public async Task InsertTimeRange_WriteIncomplete_Answers503WithArchiveAndRowCounts()
    {
        GivenInsertTimeRangeThrows(WriteIncomplete(rowsSent: 48, rowsMissing: 7));

        var result = await InsertTimeRange();

        ServiceUnavailableMessage(result).Should().Contain("7 of 48 rows").And.Contain(QualifiedTable);
    }

    [Fact]
    public async Task Insert_WriteIncomplete_BodyIsTheMessageWithoutStackTrace()
    {
        var exception = await Thrown(WriteIncomplete(rowsSent: 10, rowsMissing: 3));
        GivenInsertThrows(exception);

        var message = ServiceUnavailableMessage(await Insert());

        message.Should().Be(exception.Message);
        message.Should().NotContain(" at ").And.NotContain(nameof(StreamDataControllerWriteIncompleteTests));
    }

    [Fact]
    public async Task InsertTimeRange_WriteIncomplete_BodyIsTheMessageWithoutStackTrace()
    {
        var exception = await Thrown(WriteIncomplete(rowsSent: 48, rowsMissing: 7));
        GivenInsertTimeRangeThrows(exception);

        var message = ServiceUnavailableMessage(await InsertTimeRange());

        message.Should().Be(exception.Message);
        message.Should().NotContain(" at ").And.NotContain(nameof(StreamDataControllerWriteIncompleteTests));
    }

    // ---- other exceptions keep their mapping -------------------------------------------------------------------

    public static TheoryData<string> BadRequestExceptions => ["StreamData", "ArchiveNotFound", "Argument"];

    [Theory]
    [MemberData(nameof(BadRequestExceptions))]
    public async Task Insert_OtherRefusals_StillAnswer400(string kind)
    {
        GivenInsertThrows(BadRequestException(kind));

        var result = await Insert();

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [MemberData(nameof(BadRequestExceptions))]
    public async Task InsertTimeRange_OtherRefusals_StillAnswer400(string kind)
    {
        GivenInsertTimeRangeThrows(BadRequestException(kind));

        var result = await InsertTimeRange();

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Insert_ManualRowNotStored_StillAnswers409()
    {
        GivenInsertThrows(new ManualStreamDataNotAppliedException(_archiveRtId, ManualRowSkipped()));

        (await Insert()).Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task InsertTimeRange_ManualRowNotStored_StillAnswers409()
    {
        GivenInsertTimeRangeThrows(new ManualStreamDataNotAppliedException(_archiveRtId, ManualRowSkipped()));

        (await InsertTimeRange()).Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Insert_UnmappedException_IsNotTurnedInto503()
    {
        GivenInsertThrows(new InvalidOperationException("unexpected"));

        var act = () => Insert();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task InsertTimeRange_UnmappedException_IsNotTurnedInto503()
    {
        GivenInsertTimeRangeThrows(new InvalidOperationException("unexpected"));

        var act = () => InsertTimeRange();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    /// <summary>The constructor is internal to the engine; the controller only ever sees the thrown instance.</summary>
    private static StreamDataWriteIncompleteException WriteIncomplete(int rowsSent, int rowsMissing) =>
        (StreamDataWriteIncompleteException)typeof(StreamDataWriteIncompleteException)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single()
            .Invoke([QualifiedTable, rowsSent, rowsMissing, "The guarded upsert lacked RETURNING rows."]);

    /// <summary>Throws and catches the exception so it carries a stack trace, like the one from the repository.</summary>
    private static Task<Exception> Thrown(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception e)
        {
            e.StackTrace.Should().NotBeNullOrEmpty();
            return Task.FromResult(e);
        }
    }

    private Exception BadRequestException(string kind) => kind switch
    {
        "StreamData" => new StreamDataException("Archive is not activated.", _archiveRtId),
        "ArchiveNotFound" => new ArchiveNotFoundException(_archiveRtId),
        "Argument" => new ArgumentException("Point 0: value 'abc' of column 'Amount.Value' is not a Double."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private StreamDataWriteResult ManualRowSkipped() => new(0,
    [
        new StreamDataSkippedRow(_rtId, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), null,
            ArchiveRowOrigin.Manual, StreamDataSkipReason.LowerPrecedence)
    ]);

    private void GivenInsertThrows(Exception exception) =>
        A.CallTo(() => _repository.InsertAsync(A<OctoObjectId>._, A<IEnumerable<StreamDataPoint>>._))
            .ThrowsAsync(exception);

    private void GivenInsertTimeRangeThrows(Exception exception) =>
        A.CallTo(() => _repository.InsertTimeRangeAsync(A<OctoObjectId>._, A<IEnumerable<TimeRangeStreamDataPoint>>._,
                A<CancellationToken>._))
            .ThrowsAsync(exception);

    private Task<IActionResult> Insert() => _controller.Insert(TenantId, _archiveRtId.ToString(),
        JsonSerializer.Deserialize<IReadOnlyList<InsertRawPointRestDto>>($$"""
            [{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}", "timestamp": "2026-03-01T10:00:00Z",
               "attributes": { "Amount.Value": 1.5 } }]
            """, MvcBodyOptions)!);

    private Task<IActionResult> InsertTimeRange() => _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(),
        JsonSerializer.Deserialize<IReadOnlyList<InsertTimeRangePointRestDto>>($$"""
            [{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}",
               "from": "2026-08-31T22:00:00Z", "to": "2026-09-01T22:00:00Z",
               "attributes": { "Amount.Value": 1.5 } }]
            """, MvcBodyOptions)!);

    private static string ServiceUnavailableMessage(IActionResult result)
    {
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        return objectResult.Value.Should().BeOfType<string>().Subject;
    }
}
