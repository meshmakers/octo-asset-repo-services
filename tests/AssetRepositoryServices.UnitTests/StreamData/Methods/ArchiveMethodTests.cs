using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AssetRepositoryServices.UnitTests.StreamData.Methods;

/// <summary>
///     AB#8598: the archive CK methods answer the error codes of the streamData mutations, the host table has one row per
///     replaced mutation, the switch turns the host off, and the create input follows the source rules of createRollupArchive.
/// </summary>
public class ArchiveMethodTests
{
    private static readonly OctoObjectId ArchiveId = OctoObjectId.GenerateNewId();

    public static TheoryData<StreamDataException, string> Exceptions() => new()
    {
        { new ArchiveNotFoundException(ArchiveId), Statics.GraphQlErrorStreamDataArchiveNotFound },
        { new ArchiveNotActivatedException(ArchiveId, CkArchiveStatus.Disabled), Statics.GraphQlErrorStreamDataArchiveNotActivated },
        { new InvalidArchiveStateTransitionException(ArchiveId, CkArchiveStatus.Created, "Disable"), Statics.GraphQlErrorStreamDataInvalidTransition },
        { new ArchivePathInvalidException(ArchiveId, "Nope", "unknown path"), Statics.GraphQlErrorStreamDataPathInvalid },
        { new ArchiveActivationFailedException(ArchiveId, new InvalidOperationException("ddl")), Statics.GraphQlErrorStreamDataActivationFailed },
        { new RollupSchemaImmutableException(ArchiveId, null, CkArchiveStatus.Activated, "BucketSizeMs"), Statics.GraphQlErrorStreamDataSchemaImmutable },
        { new RollupCycleException(ArchiveId), Statics.GraphQlErrorStreamData }
    };

    [Theory]
    [MemberData(nameof(Exceptions))]
    public void AStreamDataRefusal_HasTheCodeOfTheMutation(StreamDataException exception, string expectedCode)
    {
        var failure = ArchiveMethodErrors.FailureOf(exception);

        failure.Code.Should().Be(expectedCode);
        failure.Parameters["archiveRtId"].Should().Be(ArchiveId.ToString());
        failure.Parameters[ArchiveMethodErrors.MessageParameter].Should().Be(exception.Message);
    }

    [Fact]
    public void AnInvalidTransition_CarriesTheStatusAndTheTransition_AsTheMutationExtensionsDo()
    {
        var failure = ArchiveMethodErrors.FailureOf(
            new InvalidArchiveStateTransitionException(ArchiveId, CkArchiveStatus.Created, "Disable"));

        failure.Parameters["fromStatus"].Should().Be("Created");
        failure.Parameters["attemptedTransition"].Should().Be("Disable");
    }

    [Fact]
    public void TheHostTable_HasOneRowPerReplacedMutation_WithTheGeneratedMethodIds()
    {
        ArchiveMethodHost.Handlers.Select(h => (h.Mutation, h.MethodId)).Should().BeEquivalentTo(new[]
        {
            ("activateArchive", SystemStreamDataCkIds.ArchiveActivateMethodId),
            ("disableArchive", SystemStreamDataCkIds.ArchiveDisableMethodId),
            ("enableArchive", SystemStreamDataCkIds.ArchiveEnableMethodId),
            ("retryArchiveActivation", SystemStreamDataCkIds.ArchiveRetryActivationMethodId),
            ("deleteArchive", SystemStreamDataCkIds.ArchiveDeleteArchiveMethodId),
            ("createTimeRangeArchive", SystemStreamDataCkIds.TimeRangeArchiveCreateArchiveMethodId),
            ("createRollupArchive", SystemStreamDataCkIds.RollupArchiveCreateArchiveMethodId),
            ("freezeRollupArchive", SystemStreamDataCkIds.RollupArchiveFreezeMethodId),
            ("unfreezeRollupArchive", SystemStreamDataCkIds.RollupArchiveUnfreezeMethodId),
            ("rewindRollupWatermark", SystemStreamDataCkIds.RollupArchiveRewindWatermarkMethodId),
            ("recomputeArchive", SystemStreamDataCkIds.RollupArchiveRecomputeMethodId),
            ("backfillRollupFromSource", SystemStreamDataCkIds.RollupArchiveBackfillFromSourceMethodId)
        });
        ArchiveMethodHost.Handlers.Should().OnlyContain(h => !h.MethodId.Contains("-1.", StringComparison.Ordinal),
            "the ids are version-less");
        ArchiveMethodHost.DeprecationReasonOf("deleteArchive").Should().Be("Use System.StreamData/Archive.DeleteArchive-1");
    }

    [Fact]
    public void TheRecomputeJobInfo_CarriesTheJobAsTheMutationAnswersIt()
    {
        var job = new RecomputeJobSnapshot(OctoObjectId.GenerateNewId(), ArchiveId, RecomputeJobState.Completed,
            RecomputeTrigger.Manual, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow, null, 10, 2, DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow, 1234, null, null, DateTime.UtcNow);

        var record = RollupTransitions.ToRecord(job);

        record.JobRtId.Should().Be(job.RtId.ToString());
        record.State.Should().Be(RtCkRecomputeJobStateEnum.Completed);
        (record.RowsProcessed, record.WindowsProcessed, record.DurationMs).Should().Be((10, 2, 1234));
        record.ErrorReason.Should().BeNull();
        foreach (var state in Enum.GetValues<RecomputeJobState>())
        {
            RollupTransitions.ToRecord(job with { State = state }).State.ToString().Should().Be(state.ToString());
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void TheSwitch_TurnsTheHostAndTheSystemTenantImportOnOrOff(string? enabled, bool expected)
    {
        var settings = new Dictionary<string, string?>();
        if (enabled != null)
        {
            settings["Methods:StreamData:Enabled"] = enabled;
        }

        var services = new ServiceCollection();
        var hosted = services.AddArchiveCkMethodHandlers(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        hosted.Should().Be(expected);
        services.Any(d => d.ImplementationType == typeof(SystemTenantStreamDataModelImport)).Should().Be(expected);
        services.Any(d => d.ServiceType == typeof(ICkInProcessMethodTransport)).Should().Be(expected);
    }

    [Fact]
    public void RollupSources_FollowTheRulesOfTheMutation()
    {
        var scalar = OctoObjectId.GenerateNewId().ToString();

        Sources(Input(scalar)).Should().ContainSingle()
            .Which.Should().Be(new RollupSourceReference(new OctoObjectId(scalar)));
        Error(Input()).Should().Be("required");
        Error(Input(sources: [])).Should().Be("empty");
        Error(Input(scalar, [Source(scalar)]))
            .Should().Be("both-sources-and-source-archive-rt-id");
        Error(Input(sources: [Source("nope")]))
            .Should().Be("invalid-object-id");
    }

    [Theory]
    [InlineData("\"sources\":[]", "empty")]
    [InlineData("\"sourceArchiveRtId\":null", "required")]
    [InlineData("\"sources\":[{\"sourceArchiveRtId\":\"nope\"}]", "invalid-object-id")]
    public void RollupSources_FromTheWireJson_KeepTheDifferenceBetweenEmptyAndMissing(string sourcesJson, string expected)
    {
        // AB#8707: the handler binds the generated parameter record; an explicit empty record array stays empty, a missing
        // optional one stays null, as with the plain classes before.
        var parameters = System.Text.Json.JsonSerializer.Deserialize<RollupArchiveCreateArchiveParameters>(
            "{\"archive\":{" + sourcesJson + ",\"bucketSizeMs\":60000,\"watermarkLagMs\":0,\"aggregations\":[]}}",
            CkMethodJson.Options)!;

        Error(parameters.Archive).Should().Be(expected);
    }

    [Fact]
    public void TimeRangeCreateParameters_FromTheWireJson_BindTheColumns()
    {
        var parameters = System.Text.Json.JsonSerializer.Deserialize<TimeRangeArchiveCreateArchiveParameters>(
            "{\"archive\":{\"targetCkTypeId\":\"Basic/Tree\",\"periodMs\":900000," +
            "\"columns\":[{\"path\":\"Voltage\",\"indexed\":true,\"required\":false}]}}", CkMethodJson.Options)!;

        parameters.Archive.PeriodMs.Should().Be(900000);
        parameters.Archive.Columns.Should().ContainSingle().Which.Path.Should().Be("Voltage");
    }

    [Fact]
    public void GeneratedEnums_MapToTheEngineEnumsByName()
    {
        foreach (var value in Enum.GetValues<RtCkRollupFunctionEnum>())
        {
            ArchiveMethodSupport.ToEngine<CkRollupFunction>(value).ToString().Should().Be(value.ToString());
        }

        foreach (var value in Enum.GetValues<RtBucketAlignmentEnum>())
        {
            ArchiveMethodSupport.ToEngine<BucketAlignment>(value).ToString().Should().Be(value.ToString());
        }
    }

    [Fact]
    public void AnUndefinedCkEnumValue_DoesNotMapToTheEngine()
    {
        ArchiveMethodSupport.TryToEngine<CkRollupFunction>((RtCkRollupFunctionEnum)999, out _).Should().BeFalse();
        ArchiveMethodSupport.TryToEngine<BucketAlignment>((RtBucketAlignmentEnum)999, out _).Should().BeFalse();
        ArchiveMethodSupport.TryToEngine<CkRollupFunction>(RtCkRollupFunctionEnum.Sum, out var sum).Should().BeTrue();
        sum.Should().Be(CkRollupFunction.Sum);
    }

    [Theory]
    [InlineData("\"aggregations\":[{\"sourcePath\":\"Voltage\",\"function\":999}]", "archive.aggregations.function")]
    [InlineData("\"bucketAlignment\":999,\"aggregations\":[]", "archive.bucketAlignment")]
    public async Task RollupCreate_AnUndefinedEnumNumber_IsAValidationFailureOnTheField(string json, string field)
    {
        var parameters = System.Text.Json.JsonSerializer.Deserialize<RollupArchiveCreateArchiveParameters>(
            "{\"archive\":{\"sourceArchiveRtId\":\"" + OctoObjectId.GenerateNewId() + "\",\"bucketSizeMs\":60000," +
            "\"watermarkLagMs\":0," + json + "}}", CkMethodJson.Options)!;
        var handler = new RollupArchiveCreateArchiveHandler(null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RollupArchiveCreateArchiveHandler>.Instance);

        var outcome = await handler.HandleAsync(
            new CkMethodContext<RollupArchiveCreateArchiveParameters>(new CkMethodRequest("t1")
            {
                InvocationId = Guid.NewGuid(), MethodId = SystemStreamDataCkIds.RollupArchiveCreateArchiveMethodId,
                CkTypeId = "System.StreamData/RollupArchive", DeadlineUtc = DateTime.UtcNow.AddSeconds(30)
            }, parameters), CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.Failure!.Code.Should().Be(CkMethodErrorCodes.MethodValidationFailed);
        outcome.Failure.Parameters["field"].Should().Be(field);
    }

    private static RtRollupArchiveCreateInputRecord Input(string? scalar = null,
        IEnumerable<RtCkRollupSourceReferenceRecord>? sources = null)
    {
        var input = new RtRollupArchiveCreateInputRecord { SourceArchiveRtId = scalar };
        if (sources != null)
        {
            input.Sources = new AttributeRecordValueList<RtCkRollupSourceReferenceRecord>(sources.Cast<RtRecord>().ToList());
        }

        return input;
    }

    private static RtCkRollupSourceReferenceRecord Source(string id) => new() { SourceArchiveRtId = id };

    private static IReadOnlyList<RollupSourceReference> Sources(RtRollupArchiveCreateInputRecord input) =>
        RollupArchiveCreateArchiveHandler.BuildSources(input, out _)!;

    private static string? Error(RtRollupArchiveCreateInputRecord input)
    {
        RollupArchiveCreateArchiveHandler.BuildSources(input, out var error);
        return error;
    }
}
