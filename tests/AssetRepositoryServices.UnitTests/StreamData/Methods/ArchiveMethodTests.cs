using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
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

        Sources(new RollupArchiveCreateInput { SourceArchiveRtId = scalar }).Should().ContainSingle()
            .Which.Should().Be(new RollupSourceReference(new OctoObjectId(scalar)));
        Error(new RollupArchiveCreateInput()).Should().Be("required");
        Error(new RollupArchiveCreateInput { Sources = [] }).Should().Be("empty");
        Error(new RollupArchiveCreateInput { SourceArchiveRtId = scalar, Sources = [new RollupSourceInput { SourceArchiveRtId = scalar }] })
            .Should().Be("both-sources-and-source-archive-rt-id");
        Error(new RollupArchiveCreateInput { Sources = [new RollupSourceInput { SourceArchiveRtId = "nope" }] })
            .Should().Be("invalid-object-id");
    }

    private static IReadOnlyList<RollupSourceReference> Sources(RollupArchiveCreateInput input) =>
        RollupArchiveCreateArchiveHandler.BuildSources(input, out _)!;

    private static string? Error(RollupArchiveCreateInput input)
    {
        RollupArchiveCreateArchiveHandler.BuildSources(input, out var error);
        return error;
    }
}
