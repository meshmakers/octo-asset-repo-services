using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Methods;
using static Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods.ArchiveMethodSupport;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;

/// <summary>
///     Handler of the static <c>System.StreamData/TimeRangeArchive.CreateArchive-1</c> (AB#8598). Mutation parity:
///     <c>streamData.createTimeRangeArchive</c> (<see cref="ITimeRangeArchiveRuntimeStore.InsertAsync" />): the archive is created
///     in status Created and its runtime id is the result.
/// </summary>
internal sealed class TimeRangeArchiveCreateArchiveHandler(
    ISystemContext systemContext,
    ArchiveMethodCallerTenantGuard tenantGuard,
    ILogger<TimeRangeArchiveCreateArchiveHandler> logger)
    : ICkMethodHandler<TimeRangeArchiveCreateParameters, TimeRangeArchiveCreateArchiveResult>
{
    public async Task<CkMethodOutcome<TimeRangeArchiveCreateArchiveResult>> HandleAsync(
        CkMethodContext<TimeRangeArchiveCreateParameters> context, CancellationToken cancellationToken)
    {
        if (!tenantGuard.IsAllowed(context))
        {
            return CkMethodOutcome<TimeRangeArchiveCreateArchiveResult>.Forbidden(ArchiveMethodCallerTenantGuard.Reason);
        }

        if (context.Parameters.Archive is not { } input)
        {
            return CkMethodOutcome<TimeRangeArchiveCreateArchiveResult>.ValidationFailed("archive", "required");
        }

        if (string.IsNullOrWhiteSpace(input.TargetCkTypeId))
        {
            return CkMethodOutcome<TimeRangeArchiveCreateArchiveResult>.ValidationFailed("archive.targetCkTypeId", "required");
        }

        var columns = input.Columns ?? [];
        if (columns.Any(c => string.IsNullOrWhiteSpace(c.Path)))
        {
            return CkMethodOutcome<TimeRangeArchiveCreateArchiveResult>.ValidationFailed("archive.columns.path", "required");
        }

        var tenant = await FindTenantAsync(systemContext, context).ConfigureAwait(false);
        if (tenant?.GetTimeRangeArchiveRuntimeStore() is not { } store)
        {
            return CkMethodOutcome<TimeRangeArchiveCreateArchiveResult>.Fail(ArchiveMethodErrors.StreamDataNotEnabled);
        }

        logger.LogDebug(
            "TimeRangeArchive Create requested for target {TargetCkTypeId} ({ColumnCount} columns, CK method, invocation " +
            "{InvocationId})", input.TargetCkTypeId, columns.Count, context.InvocationId);
        try
        {
            var rtId = await store.InsertAsync(
                input.RtWellKnownName,
                new RtCkId<CkTypeId>(input.TargetCkTypeId),
                columns.Select(c => new CkArchiveColumnSpec(c.Path!, c.Indexed, c.Required)).ToList(),
                input.PeriodMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null).ConfigureAwait(false);
            return new TimeRangeArchiveCreateArchiveResult { Value = rtId.ToString() };
        }
        catch (StreamDataException e)
        {
            logger.LogInformation("TimeRangeArchive Create refused (CK method): {Reason}", e.Message);
            return CkMethodOutcome<TimeRangeArchiveCreateArchiveResult>.Fail(ArchiveMethodErrors.FailureOf(e));
        }
    }
}

/// <summary>
///     Handler of the static <c>System.StreamData/RollupArchive.CreateArchive-1</c> (AB#8598). Mutation parity:
///     <c>streamData.createRollupArchive</c> (<see cref="IRollupArchiveLifecycleService.CreateAsync" />): the same source rules
///     (exactly one of <c>sources</c> and the deprecated <c>sourceArchiveRtId</c>, a non-empty list), the same defaults and the
///     same engine validation. An input the mutation answers with a validation message is <c>METHOD_VALIDATION_FAILED</c> here,
///     naming the field.
/// </summary>
internal sealed class RollupArchiveCreateArchiveHandler(
    ISystemContext systemContext,
    ArchiveMethodCallerTenantGuard tenantGuard,
    ILogger<RollupArchiveCreateArchiveHandler> logger)
    : ICkMethodHandler<RollupArchiveCreateParameters, RollupArchiveCreateArchiveResult>
{
    public async Task<CkMethodOutcome<RollupArchiveCreateArchiveResult>> HandleAsync(
        CkMethodContext<RollupArchiveCreateParameters> context, CancellationToken cancellationToken)
    {
        if (!tenantGuard.IsAllowed(context))
        {
            return CkMethodOutcome<RollupArchiveCreateArchiveResult>.Forbidden(ArchiveMethodCallerTenantGuard.Reason);
        }

        if (context.Parameters.Archive is not { } input)
        {
            return CkMethodOutcome<RollupArchiveCreateArchiveResult>.ValidationFailed("archive", "required");
        }

        var sources = BuildSources(input, out var sourcesError);
        if (sourcesError is not null)
        {
            return CkMethodOutcome<RollupArchiveCreateArchiveResult>.ValidationFailed("archive.sources", sourcesError);
        }

        var aggregations = input.Aggregations ?? [];
        if (aggregations.Any(a => string.IsNullOrWhiteSpace(a.SourcePath)))
        {
            return CkMethodOutcome<RollupArchiveCreateArchiveResult>.ValidationFailed("archive.aggregations.sourcePath",
                "required");
        }

        var tenant = await FindTenantAsync(systemContext, context).ConfigureAwait(false);
        if (tenant?.GetRollupArchiveLifecycleService() is not { } lifecycle)
        {
            return CkMethodOutcome<RollupArchiveCreateArchiveResult>.Fail(ArchiveMethodErrors.StreamDataNotEnabled);
        }

        logger.LogDebug(
            "Rollup Create requested for {SourceCount} source(s) ({AggregationCount} aggregations, CK method, invocation " +
            "{InvocationId})", sources!.Count, aggregations.Count, context.InvocationId);
        try
        {
            var rtId = await lifecycle.CreateAsync(
                input.RtWellKnownName,
                sources,
                TimeSpan.FromMilliseconds(input.BucketSizeMs),
                TimeSpan.FromMilliseconds(input.WatermarkLagMs),
                aggregations.Select(a => new CkRollupAggregationSpec(a.SourcePath!, a.Function,
                    a.TargetColumnName, a.ComparisonValue)).ToList(),
                input.BucketAlignment ?? BucketAlignment.FixedSize,
                input.ReferenceTimeZone,
                input.CarryLookbackMs is { } carryMs ? TimeSpan.FromMilliseconds(carryMs) : null,
                input.DayStartOffsetMs).ConfigureAwait(false);
            return new RollupArchiveCreateArchiveResult { Value = rtId.ToString() };
        }
        catch (ArgumentException e) when (e.ParamName == "referenceTimeZone")
        {
            // AB#7361: an unknown zone is a client input error (the mutation names the field as well).
            return CkMethodOutcome<RollupArchiveCreateArchiveResult>.ValidationFailed("archive.referenceTimeZone",
                "unknown-time-zone");
        }
        catch (StreamDataException e)
        {
            logger.LogInformation("Rollup Create refused (CK method): {Reason}", e.Message);
            return CkMethodOutcome<RollupArchiveCreateArchiveResult>.Fail(ArchiveMethodErrors.FailureOf(e));
        }
    }

    /// <summary>
    ///     The source rules of the mutation (<c>StreamDataMutation.BuildRollupSources</c>, AB#5157): an explicitly empty list is
    ///     an error of its own, both forms or neither are refused, the deprecated scalar becomes one unbounded source.
    /// </summary>
    internal static IReadOnlyList<RollupSourceReference>? BuildSources(RollupArchiveCreateInput input,
        out string? error)
    {
        error = null;
        var list = input.Sources;
        var hasScalar = !string.IsNullOrWhiteSpace(input.SourceArchiveRtId);
        if (list is { Count: 0 })
        {
            error = "empty";
            return null;
        }

        if (hasScalar && list is { Count: > 0 })
        {
            error = "both-sources-and-source-archive-rt-id";
            return null;
        }

        if (!hasScalar && list is null)
        {
            error = "required";
            return null;
        }

        if (hasScalar)
        {
            if (!OctoObjectId.TryParse(input.SourceArchiveRtId!, out var scalar))
            {
                error = "invalid-object-id";
                return null;
            }

            return [new RollupSourceReference(scalar)];
        }

        var sources = new List<RollupSourceReference>(list!.Count);
        foreach (var source in list)
        {
            if (source.SourceArchiveRtId is null || !OctoObjectId.TryParse(source.SourceArchiveRtId, out var sourceRtId))
            {
                error = "invalid-object-id";
                return null;
            }

            sources.Add(new RollupSourceReference(sourceRtId, source.ValidFrom, source.ValidTo));
        }

        return sources;
    }
}

// ---- Parameter shapes -----------------------------------------------------------------------------------------------
// The generated {Type}{Method}Parameters of System.StreamData carry the Record parameter as the generated Rt{Record}Record, an
// attribute-bag type whose record arrays are IAttributeValueList<T> (an interface). The method JSON (System.Text.Json,
// CkMethodJson) cannot bind an interface collection, so a call with a record array answered METHOD_HANDLER_FAILED. These plain
// classes have the same members as the CK records (TimeRangeArchiveCreateInput, ArchiveColumnSpec, RollupArchiveCreateInput,
// CkRollupSourceReference, CkRollupAggregation); the registration validation checks the parameter names against the model.

/// <summary>Parameters of <c>TimeRangeArchive.CreateArchive-1</c> (CK record <c>TimeRangeArchiveCreateInput</c>).</summary>
internal sealed class TimeRangeArchiveCreateParameters
{
    public TimeRangeArchiveCreateInput? Archive { get; set; }
}

/// <summary>CK record <c>System.StreamData/TimeRangeArchiveCreateInput</c>.</summary>
internal sealed class TimeRangeArchiveCreateInput
{
    public string? RtWellKnownName { get; set; }
    public string? TargetCkTypeId { get; set; }
    public List<ArchiveColumnSpecInput>? Columns { get; set; }
    public long? PeriodMs { get; set; }
}

/// <summary>CK record <c>System.StreamData/ArchiveColumnSpec</c>.</summary>
internal sealed class ArchiveColumnSpecInput
{
    public string? Path { get; set; }
    public bool Indexed { get; set; }
    public bool Required { get; set; }
}

/// <summary>Parameters of <c>RollupArchive.CreateArchive-1</c> (CK record <c>RollupArchiveCreateInput</c>).</summary>
internal sealed class RollupArchiveCreateParameters
{
    public RollupArchiveCreateInput? Archive { get; set; }
}

/// <summary>CK record <c>System.StreamData/RollupArchiveCreateInput</c>.</summary>
internal sealed class RollupArchiveCreateInput
{
    public string? RtWellKnownName { get; set; }
    public string? SourceArchiveRtId { get; set; }
    public List<RollupSourceInput>? Sources { get; set; }
    public long BucketSizeMs { get; set; }
    public long WatermarkLagMs { get; set; }
    public BucketAlignment? BucketAlignment { get; set; }
    public string? ReferenceTimeZone { get; set; }
    public long? CarryLookbackMs { get; set; }
    public long? DayStartOffsetMs { get; set; }
    public List<RollupAggregationInput>? Aggregations { get; set; }
}

/// <summary>CK record <c>System.StreamData/CkRollupSourceReference</c>.</summary>
internal sealed class RollupSourceInput
{
    public string? SourceArchiveRtId { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
}

/// <summary>CK record <c>System.StreamData/CkRollupAggregation</c>.</summary>
internal sealed class RollupAggregationInput
{
    public string? SourcePath { get; set; }
    public CkRollupFunction Function { get; set; }
    public string? TargetColumnName { get; set; }
    public string? ComparisonValue { get; set; }
}
