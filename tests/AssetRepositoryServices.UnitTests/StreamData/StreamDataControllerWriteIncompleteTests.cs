using System.Globalization;
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
///     answer 503 with a short problem body (archive rtId, rows sent, rows missing) and a <c>Retry-After</c> header, so
///     the caller sends the batch again. The CrateDB detail (schema, table, breaker text) is logged, never returned. The
///     other exceptions of the write keep their mapping.
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
    private readonly CapturingLogger _logger = new();
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
            _logger,
            systemContext,
            A.Fake<IHostApplicationLifetime>());
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
    }

    // ---- 503 for an incomplete write ---------------------------------------------------------------------------

    [Fact]
    public async Task Insert_WriteIncomplete_Answers503WithArchiveAndRowCounts()
    {
        GivenInsertThrows(WriteIncomplete(rowsSent: 10, rowsMissing: 3));

        var problem = ServiceUnavailableProblem(await Insert());

        problem.Extensions["archiveRtId"].Should().Be(_archiveRtId.ToString());
        problem.Extensions["rowsSent"].Should().Be(10);
        problem.Extensions["rowsMissing"].Should().Be(3);
        problem.Detail.Should().Contain("3 of 10 rows");
        RetryAfter().Should().Be(StreamDataController.WriteIncompleteRetryAfterSeconds.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task InsertTimeRange_WriteIncomplete_Answers503WithArchiveAndRowCounts()
    {
        GivenInsertTimeRangeThrows(WriteIncomplete(rowsSent: 48, rowsMissing: 7));

        var problem = ServiceUnavailableProblem(await InsertTimeRange());

        problem.Extensions["archiveRtId"].Should().Be(_archiveRtId.ToString());
        problem.Extensions["rowsSent"].Should().Be(48);
        problem.Extensions["rowsMissing"].Should().Be(7);
        problem.Detail.Should().Contain("7 of 48 rows");
        RetryAfter().Should().Be(StreamDataController.WriteIncompleteRetryAfterSeconds.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Insert_WriteIncomplete_BodyNamesNoTableNoCrateDbDetailAndNoStackTrace()
    {
        var exception = await Thrown(WriteIncomplete(rowsSent: 10, rowsMissing: 3));
        GivenInsertThrows(exception);

        var body = SerializedBody(await Insert());

        AssertNoInternals(body);
    }

    [Fact]
    public async Task InsertTimeRange_WriteIncomplete_BodyNamesNoTableNoCrateDbDetailAndNoStackTrace()
    {
        var exception = await Thrown(WriteIncomplete(rowsSent: 48, rowsMissing: 7));
        GivenInsertTimeRangeThrows(exception);

        var body = SerializedBody(await InsertTimeRange());

        AssertNoInternals(body);
    }

    [Fact]
    public async Task Insert_WriteIncomplete_LogsTheFullDetailServerSide()
    {
        GivenInsertThrows(WriteIncomplete(rowsSent: 10, rowsMissing: 3));

        await Insert();

        var logged = _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Subject;
        logged.Message.Should().Contain(QualifiedTable).And.Contain("RETURNING").And.Contain("3 of 10 rows");
        logged.Exception.Should().BeOfType<StreamDataWriteIncompleteException>();
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

    /// <summary>
    /// Builds the exception the way the engine's write path does, through its factory <c>AfterRetry</c>. The factory
    /// is internal to the engine (the controller only ever sees the thrown instance), so it is called by name; its
    /// constructor is private and changes with the message wording, so the test never calls it.
    /// </summary>
    private static StreamDataWriteIncompleteException WriteIncomplete(int rowsSent, int rowsMissing)
    {
        var afterRetry = typeof(StreamDataWriteIncompleteException).GetMethod(
            "AfterRetry",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(string), typeof(int), typeof(int), typeof(string)]);
        afterRetry.Should().NotBeNull("the engine builds this exception through StreamDataWriteIncompleteException.AfterRetry");

        return (StreamDataWriteIncompleteException)afterRetry!.Invoke(
            null, [QualifiedTable, rowsSent, rowsMissing, "The guarded upsert lacked RETURNING rows."])!;
    }

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

    private static ProblemDetails ServiceUnavailableProblem(IActionResult result)
    {
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        return problem;
    }

    private static string SerializedBody(IActionResult result) =>
        JsonSerializer.Serialize(ServiceUnavailableProblem(result), MvcBodyOptions);

    private static void AssertNoInternals(string body)
    {
        body.Should().NotContain(QualifiedTable).And.NotContain("archive_6720a1b2c3d4e5f600000001")
            .And.NotContain("maco.").And.NotContain("CrateDB").And.NotContain("breaker")
            .And.NotContain("RETURNING").And.NotContain(" at ").And.NotContain(nameof(StreamDataControllerWriteIncompleteTests));
    }

    private string? RetryAfter() => _controller.Response.Headers.RetryAfter.ToString();

    /// <summary>Keeps every log entry with its rendered message, so the test can see what the server logs.</summary>
    private sealed class CapturingLogger : ILogger<StreamDataController>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
