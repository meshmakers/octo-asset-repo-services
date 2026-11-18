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

    /// <summary>
    ///     AB#8600 / AB#6247: addArchiveColumn refuses a path that reaches a Hidden attribute with STREAMDATA_ERROR and the
    ///     engine message; Archive.AddColumn-1 answers the same.
    /// </summary>
    [Fact]
    public void AHiddenAttribute_IsRefusedWithTheCodeOfTheMutation()
    {
        var exception = new HiddenAttributeInArchiveException(ArchiveId, new RtCkId<CkTypeId>("Basic/Tree"), "Secret",
            "Secret");

        var failure = ArchiveMethodErrors.FailureOf(exception);

        failure.Code.Should().Be(Statics.GraphQlErrorStreamData);
        failure.Parameters[ArchiveMethodErrors.MessageParameter].Should().Contain("Hidden attribute 'Secret'");
    }

    /// <summary>
    ///     AB#8600: every field of StreamDataMutations is deprecated with the id of the CK method that replaces it, and every
    ///     method of the host that replaces a mutation names an existing field. A new hand-written streamData mutation without a
    ///     method fails here.
    /// </summary>
    [Fact]
    public void EveryStreamDataMutation_IsDeprecated_WithTheMethodThatReplacesIt()
    {
        var mutation = new StreamDataMutation(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StreamDataMutation>.Instance);
        var methodIds = ArchiveMethodHost.Handlers.Where(h => h.Mutation != null)
            .ToDictionary(h => h.Mutation!, h => h.MethodId, StringComparer.Ordinal);

        mutation.Fields.Should().NotBeEmpty();
        foreach (var field in mutation.Fields)
        {
            methodIds.Should().ContainKey(field.Name, $"streamData.{field.Name} needs a CK method (F4.3)");
            field.DeprecationReason.Should().Be($"Use {methodIds[field.Name]}", field.Name);
            field.DeprecationReason.Should().StartWith("Use System.StreamData/", field.Name);
        }

        mutation.Fields.Select(f => f.Name).Should().BeEquivalentTo(methodIds.Keys,
            "every method that replaces a mutation names a field of StreamDataMutations");
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
            // System.StreamData 1.30.0: the mutations name the asynchronous -2 versions; -1 are deprecated and kept
            ((string?)null, SystemStreamDataCkIds.RollupArchiveRecomputeMethodId),
            ((string?)null, SystemStreamDataCkIds.RollupArchiveBackfillFromSourceMethodId),
            ("recomputeArchive", SystemStreamDataCkIds.RollupArchiveRecompute2MethodId),
            ("backfillRollupFromSource", SystemStreamDataCkIds.RollupArchiveBackfillFromSource2MethodId),
            // the redaction of Hidden columns replaces no mutation
            ((string?)null, SystemStreamDataCkIds.ArchiveRedactHiddenColumnsMethodId),
            ((string?)null, SystemStreamDataCkIds.ArchiveRedactHiddenColumns2MethodId),
            // AB#8600 (System.StreamData 1.28.0): columns and computed columns
            ("addComputedColumn", SystemStreamDataCkIds.ArchiveAddComputedColumnMethodId),
            ("addArchiveColumn", SystemStreamDataCkIds.ArchiveAddColumnMethodId),
            ("removeComputedColumn", SystemStreamDataCkIds.ArchiveRemoveComputedColumnMethodId),
            ("updateComputedColumnFormula", SystemStreamDataCkIds.ArchiveUpdateComputedColumnFormulaMethodId),
            ("retryComputedColumnBackfill", SystemStreamDataCkIds.ArchiveRetryComputedColumnBackfillMethodId),
            ("repairComputedColumn", SystemStreamDataCkIds.ArchiveRepairComputedColumnMethodId)
        });
        ArchiveMethodHost.Handlers.Should().OnlyContain(h => !h.MethodId.Contains("-1.", StringComparison.Ordinal),
            "the ids are version-less");
        ArchiveMethodHost.DeprecationReasonOf("deleteArchive").Should().Be("Use System.StreamData/Archive.DeleteArchive-1");
        ArchiveMethodHost.DeprecationReasonOf("recomputeArchive").Should().Be("Use System.StreamData/RollupArchive.Recompute-2");
        ArchiveMethodHost.DeprecationReasonOf("backfillRollupFromSource")
            .Should().Be("Use System.StreamData/RollupArchive.BackfillFromSource-2");
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
        record.ComputedColumnName.Should().BeNull("a rollup recompute writes no computed column");
        RollupTransitions.ToRecord(job with { StagingTableName = RecomputeJobSnapshot.ComputedColumnMarkerPrefix + "ratio" })
            .ComputedColumnName.Should().Be("ratio",
            "AB#8600: the job of a computed column method names the column, as computedColumnName of the mutation");
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

    /// <summary>AB#8600: an undefined result type number is a validation failure on <c>resultType</c>; nothing is called.</summary>
    [Fact]
    public async Task AddComputedColumn_AnUndefinedResultType_IsAValidationFailureOnTheField()
    {
        var handler = new ArchiveAddComputedColumnHandler(null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ArchiveAddComputedColumnHandler>.Instance);
        var parameters = new ArchiveAddComputedColumnParameters
        {
            Name = "x", Formula = "Voltage", ResultType = (RtCkComputedColumnResultTypeEnum)999
        };

        var outcome = await handler.HandleAsync(
            new CkMethodContext<ArchiveAddComputedColumnParameters>(new CkMethodRequest("t1")
            {
                InvocationId = Guid.NewGuid(), MethodId = SystemStreamDataCkIds.ArchiveAddComputedColumnMethodId,
                CkTypeId = "System.StreamData/RawArchive", TargetRtId = OctoObjectId.GenerateNewId().ToString(),
                DeadlineUtc = DateTime.UtcNow.AddSeconds(30)
            }, parameters), CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.Failure!.Code.Should().Be(CkMethodErrorCodes.MethodValidationFailed);
        outcome.Failure.Parameters["field"].Should().Be("resultType");
        foreach (var value in Enum.GetValues<RtCkComputedColumnResultTypeEnum>())
        {
            ArchiveMethodSupport.ToEngine<Meshmakers.Octo.Runtime.Contracts.Formulas.FormulaResultType>(value).ToString()
                .Should().Be(value.ToString());
        }
    }

    /// <summary>
    ///     AB#9057: the REST facade of AddComputedColumn-1 passes every result type of the REST contract to the method by name
    ///     and back unchanged; a number outside the enum stays that number, which the method refuses as a validation failure.
    /// </summary>
    [Fact]
    public void ResultTypeOf_MapsEveryRestResultTypeByName_AndKeepsAnUndefinedNumber()
    {
        foreach (var value in Enum.GetValues<Meshmakers.Octo.Runtime.Contracts.Formulas.FormulaResultType>())
        {
            var ckValue = ArchiveColumnChanges.ResultTypeOf(value);
            Enum.IsDefined(ckValue).Should().BeTrue(value.ToString());
            ckValue.ToString().Should().Be(value.ToString());
            ArchiveMethodSupport.ToEngine<Meshmakers.Octo.Runtime.Contracts.Formulas.FormulaResultType>(ckValue).Should().Be(value);
        }

        var undefined = ArchiveColumnChanges.ResultTypeOf((Meshmakers.Octo.Runtime.Contracts.Formulas.FormulaResultType)999);
        Enum.IsDefined(undefined).Should().BeFalse();
        ArchiveMethodSupport.TryToEngine<Meshmakers.Octo.Runtime.Contracts.Formulas.FormulaResultType>(undefined, out _)
            .Should().BeFalse("the handler answers METHOD_VALIDATION_FAILED on resultType");
    }

    /// <summary>
    ///     AB#9057: the job a retry or repair method answers reaches REST as the same <c>RecomputeJobInfoRestDto</c> the direct
    ///     call built from the engine snapshot; a result without a job is no job (the facade answers 204).
    /// </summary>
    [Fact]
    public void JobOf_ReadsTheJobOfTheMethodResult_AsTheDirectCallMappedTheSnapshot()
    {
        var snapshot = new RecomputeJobSnapshot(OctoObjectId.GenerateNewId(), OctoObjectId.GenerateNewId(),
            RecomputeJobState.Running, RecomputeTrigger.Manual, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), null, 12, 3,
            new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc), null, null, null,
            RecomputeJobSnapshot.ComputedColumnMarkerPrefix + RecomputeJobSnapshot.ComputedColumnRepairMarker + "fixed",
            new DateTime(2026, 3, 1, 8, 1, 0, DateTimeKind.Utc));
        var result = new ArchiveRepairComputedColumnResult { Value = RollupTransitions.ToRecord(snapshot) };
        var response = CkMethodResponse.Succeeded(Guid.NewGuid(), "h",
            System.Text.Json.JsonSerializer.Serialize(result, CkMethodJson.Options));

        var job = ArchiveColumnChanges.JobOf(response);

        job.Should().NotBeNull();
        var viaMethod = Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers.RecomputeJobInfoRestDto.From(job!);
        var direct = Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers.RecomputeJobInfoRestDto.From(snapshot);
        viaMethod.Should().BeEquivalentTo(direct);
        viaMethod.ComputedColumnName.Should().Be("fixed");

        var noJob = CkMethodResponse.Succeeded(Guid.NewGuid(), "h",
            System.Text.Json.JsonSerializer.Serialize(new ArchiveRetryComputedColumnBackfillResult { Value = null },
                CkMethodJson.Options));
        ArchiveColumnChanges.JobOf(noJob).Should().BeNull();
        ArchiveColumnChanges.JobOf(CkMethodResponse.Succeeded(Guid.NewGuid(), "h")).Should().BeNull();
        ArchiveColumnChanges.JobOf(CkMethodResponse.Failed(Guid.NewGuid(), "h",
            CkMethodError.Create(CkMethodErrorCodes.MethodForbidden))).Should().BeNull();
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
