using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
/// Custom archive lifecycle mutations (concept §16). Each mutation maps to one transition on
/// <see cref="IArchiveLifecycleService"/>; exceptions from §12 are surfaced via stable GraphQL
/// error codes by <see cref="ResolveConnectionContextExtensions.HandleException"/>.
/// </summary>
[DoNotRegister]
internal sealed class StreamDataMutation : ObjectGraphType
{
    private readonly ILogger<StreamDataMutation> _logger;

    public StreamDataMutation(ILogger<StreamDataMutation> logger)
    {
        _logger = logger;
        Name = "StreamDataMutations";
        Description = "Archive lifecycle mutations: activate, disable, enable, retry activation, delete.";

        Field<NonNullGraphType<ArchiveTransitionResultDtoType>>("activateArchive")
            .Description("Provisions the Crate table and transitions the archive to Activated. Allowed from Created/Disabled/Failed; idempotent on Activated.")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "Runtime id of the CkArchive to activate.")
            .ResolveAsync(ctx => ResolveTransitionAsync(ctx, "Activate",
                (svc, id) => svc.ActivateAsync(id)));

        Field<NonNullGraphType<ArchiveTransitionResultDtoType>>("disableArchive")
            .Description("Transitions the archive to Disabled. Allowed only from Activated; the Crate table is preserved.")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "Runtime id of the CkArchive to disable.")
            .ResolveAsync(ctx => ResolveTransitionAsync(ctx, "Disable",
                (svc, id) => svc.DisableAsync(id)));

        Field<NonNullGraphType<ArchiveTransitionResultDtoType>>("enableArchive")
            .Description("Transitions the archive from Disabled back to Activated. Re-validates column paths against the current CK model.")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "Runtime id of the CkArchive to enable.")
            .ResolveAsync(ctx => ResolveTransitionAsync(ctx, "Enable",
                (svc, id) => svc.EnableAsync(id)));

        Field<NonNullGraphType<ArchiveTransitionResultDtoType>>("retryArchiveActivation")
            .Description("Retries activation after a previous DDL failure. Allowed only from Failed.")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "Runtime id of the CkArchive to retry activation for.")
            .ResolveAsync(ctx => ResolveTransitionAsync(ctx, "RetryActivation",
                (svc, id) => svc.RetryActivationAsync(id)));

        Field<NonNullGraphType<BooleanGraphType>>("deleteArchive")
            .Description("Drops the per-archive CrateDB table (idempotent) and soft-deletes the CkArchive entity. Destructive — historical data is lost. Allowed from any status. Returns true when the archive was deleted.")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "Runtime id of the CkArchive to delete.")
            .ResolveAsync(ResolveDeleteAsync);

        // ---- TimeRangeArchive create (concept-time-range §10) ----

        Field<NonNullGraphType<OctoObjectIdType>>("createTimeRangeArchive")
            .Description("Creates a new TimeRangeArchive in Created status. Takes target CK type, attribute-path columns, and optional advisory period. Unlike createRollupArchive there is no source archive to inherit anything from — the operator picks everything directly. Returns the generated rtId. Requires StreamDataAdmin.")
            .Argument<NonNullGraphType<CreateTimeRangeArchiveInputType>>("input", "Time-range-specific create payload.")
            .ResolveAsync(ResolveCreateTimeRangeAsync);

        // ---- Rollup-only mutations (rollup-archives concept §9) ----

        Field<NonNullGraphType<OctoObjectIdType>>("createRollupArchive")
            .Description("Creates a new CkRollupArchive in Created status. The inherited CkArchive attributes (TargetCkTypeId, Columns) are resolved server-side from the source archive and the supplied aggregations (RollupColumnGenerator). Returns the generated rtId.")
            .Argument<NonNullGraphType<CreateRollupArchiveInputType>>("input", "Rollup-specific create payload.")
            .ResolveAsync(ResolveCreateRollupAsync);

        Field<NonNullGraphType<ArchiveTransitionResultDtoType>>("freezeRollupArchive")
            .Description("Sets FrozenUntil on the rollup archive. Monotonic — rejected when the new value is earlier than the current FrozenUntil (use unfreezeRollupArchive instead). When set, the orchestrator stops producing buckets whose bucketEnd falls within the frozen range.")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "Runtime id of the CkRollupArchive to freeze.")
            .Argument<NonNullGraphType<DateTimeGraphType>>("until", "Inclusive upper bound of the frozen range, ISO-8601.")
            .ResolveAsync(ctx => ResolveRollupAsync(ctx, "Freeze",
                (svc, id) => svc.FreezeAsync(id, ctx.GetArgument<DateTime>("until"))));

        Field<NonNullGraphType<ArchiveTransitionResultDtoType>>("unfreezeRollupArchive")
            .Description("Clears FrozenUntil on the rollup archive. Idempotent. The optional acceptGaps flag is recorded but the gap-detection guard is not yet enforced (concept §9 follow-up).")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "Runtime id of the CkRollupArchive to unfreeze.")
            .Argument<BooleanGraphType>("acceptGaps", "When the source data inside the previously frozen range has been truncated, acknowledge that unfreezing will produce gaps. Defaults to false.")
            .ResolveAsync(ctx => ResolveRollupAsync(ctx, "Unfreeze",
                (svc, id) => svc.UnfreezeAsync(id, ctx.GetArgument<bool?>("acceptGaps") ?? false)));

        Field<NonNullGraphType<ArchiveTransitionResultDtoType>>("rewindRollupWatermark")
            .Description("Resets LastAggregatedBucketEnd to the given timestamp (truncated to the bucket boundary). Subsequent orchestrator ticks re-aggregate the rewound range. Destructive: previously committed rows in that range are temporarily out of sync until the orchestrator catches up.")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "Runtime id of the CkRollupArchive to rewind.")
            .Argument<NonNullGraphType<DateTimeGraphType>>("toBucketEnd", "Target bucket-end (exclusive) the watermark is reset to, ISO-8601. Will be truncated down to the bucket boundary.")
            .ResolveAsync(ctx => ResolveRollupAsync(ctx, "RewindWatermark",
                (svc, id) => svc.RewindWatermarkAsync(id, ctx.GetArgument<DateTime>("toBucketEnd"))));

        Field<NonNullGraphType<RecomputeJobInfoDtoType>>("recomputeArchive")
            .Description("Triggers (or coalesces) an optimistic recompute of a rollup archive over the half-open range [from, to). Returns the resulting job snapshot. While a recompute runs, readers keep seeing a consistent snapshot. Requires StreamDataAdmin. AB#4184.")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "Runtime id of the CkRollupArchive to recompute.")
            .Argument<NonNullGraphType<DateTimeGraphType>>("from", "Inclusive range start, ISO-8601.")
            .Argument<NonNullGraphType<DateTimeGraphType>>("to", "Exclusive range end, ISO-8601.")
            .Argument<OctoObjectIdType>("rtIdScope", "Optional: restrict the recompute to a single entity (metering point / stream).")
            .ResolveAsync(ResolveRecomputeAsync);

        Field<RecomputeJobInfoDtoType>("backfillRollupFromSource")
            .Description("Queues a durable, background backfill that populates / resets a rollup over the ENTIRE history of its source archive without supplying a timestamp (AB#4269 / AB#4286). Resolves the source archive's earliest timestamp, enqueues a persisted pending recompute range [sourceMin, now) and a Pending RecomputeJob, and returns that job immediately. The heavy recompute runs later on the background orchestrator under the host application-lifetime token — not this request — so a client timeout can no longer cancel a long backfill and the queued work survives a restart. Poll the returned job to observe Pending → Running → Completed. Returns null when the source archive holds no data. Requires StreamDataAdmin.")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "Runtime id of the CkRollupArchive to backfill from its source.")
            .ResolveAsync(ResolveBackfillRollupAsync);

        // ---- Computed columns (AB#4189 Phase 7) ----

        Field<NonNullGraphType<ArchiveTransitionResultDtoType>>("addComputedColumn")
            .Description("Adds a computed column to an Activated raw or time-range archive and backfills it across the existing rows. The column stays hidden until the backfill completes, then becomes visible atomically; a backfill failure leaves the previous archive state intact. Requires StreamDataAdmin. AB#4189.")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "Runtime id of the archive to add the computed column to.")
            .Argument<NonNullGraphType<StringGraphType>>("name", "Output column name — the identifier formulas reference and the column appears under.")
            .Argument<NonNullGraphType<StringGraphType>>("formula", "mXparser formula over other columns of the same row (e.g. 'activePower / apparentPower').")
            .Argument<NonNullGraphType<EnumerationGraphType<FormulaResultType>>>("resultType", "Declared type the formula result is cast back to (Boolean / Int / Int64 / Double / DateTime).")
            .Argument<BooleanGraphType>("indexed", "Whether the physical column is indexed. Defaults to true.")
            .ResolveAsync(ResolveAddComputedColumnAsync);

        Field<NonNullGraphType<ArchiveTransitionResultDtoType>>("removeComputedColumn")
            .Description("Removes a computed column from an archive. Rejected when another computed column still references it. The physical CrateDB column is left as a harmless orphan the read path no longer projects. Requires StreamDataAdmin. AB#4189.")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "Runtime id of the archive to remove the computed column from.")
            .Argument<NonNullGraphType<StringGraphType>>("name", "Name of the computed column to remove.")
            .ResolveAsync(ResolveRemoveComputedColumnAsync);

        Field<NonNullGraphType<ArchiveTransitionResultDtoType>>("updateComputedColumnFormula")
            .Description("Changes the formula of an existing computed column on an active archive with optimistic / atomic semantics: readers keep seeing the previous formula's values while the new one is backfilled, then switch atomically. Rejected when another computed column references this one (it would orphan the reference) — re-point or remove the dependent first. The result type is unchanged. Requires StreamDataAdmin. AB#4189.")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "Runtime id of the archive carrying the computed column.")
            .Argument<NonNullGraphType<StringGraphType>>("name", "Name of the computed column to re-formulate.")
            .Argument<NonNullGraphType<StringGraphType>>("formula", "The new mXparser formula.")
            .ResolveAsync(ResolveUpdateComputedColumnFormulaAsync);
    }

    private async Task<object?> ResolveRecomputeAsync(IResolveFieldContext<object?> ctx)
    {
        try
        {
            var rollupRtId = ctx.GetArgument<OctoObjectId>(Statics.RtIdArg);
            var from = ctx.GetArgument<DateTime>("from");
            var to = ctx.GetArgument<DateTime>("to");
            var rtIdScope = ctx.GetArgument<OctoObjectId?>("rtIdScope");

            var gql = (GraphQlUserContext)ctx.UserContext;
            if (gql.User?.IsInRole(CommonConstants.StreamDataAdminRole) != true)
            {
                ctx.Errors.Add(new ExecutionError(
                    $"Recompute requires the '{CommonConstants.StreamDataAdminRole}' role.")
                {
                    Code = Statics.GraphQlForbidden,
                });
                return null;
            }

            var orchestrator = gql.TenantContext.GetRecomputeOrchestrator()
                ?? throw AssetRepositoryException.StreamDataNotAvailable();

            var job = await orchestrator.RecomputeArchiveAsync(
                rollupRtId, from, to, rtIdScope, RecomputeTrigger.Manual, ctx.CancellationToken);

            return RecomputeJobInfoDto.From(job);
        }
        catch (Exception e)
        {
            return ctx.HandleException(e);
        }
    }

    private async Task<object?> ResolveBackfillRollupAsync(IResolveFieldContext<object?> ctx)
    {
        try
        {
            var rollupRtId = ctx.GetArgument<OctoObjectId>(Statics.RtIdArg);
            _logger.LogDebug("Backfill-from-source requested for rollup {RollupRtId}", rollupRtId);

            var gql = (GraphQlUserContext)ctx.UserContext;
            if (gql.User?.IsInRole(CommonConstants.StreamDataAdminRole) != true)
            {
                ctx.Errors.Add(new ExecutionError(
                    $"Backfill requires the '{CommonConstants.StreamDataAdminRole}' role.")
                {
                    Code = Statics.GraphQlForbidden,
                });
                return null;
            }

            var orchestrator = gql.TenantContext.GetRecomputeOrchestrator()
                ?? throw AssetRepositoryException.StreamDataNotAvailable();

            // AB#4286: enqueue a durable background backfill and return the Pending job immediately;
            // the heavy recompute runs later on the background orchestrator under the host
            // application-lifetime token, not this GraphQL request. ctx.CancellationToken only bounds
            // the fast source-min resolve + enqueue writes.
            var job = await orchestrator.EnqueueBackfillFromSourceAsync(rollupRtId, ctx.CancellationToken);

            // Null = empty source archive (no-op); surface as a null job rather than an error.
            return job is null ? null : RecomputeJobInfoDto.From(job);
        }
        catch (Exception e)
        {
            return ctx.HandleException(e);
        }
    }

    private async Task<object?> ResolveAddComputedColumnAsync(IResolveFieldContext<object?> ctx)
    {
        try
        {
            var archiveRtId = ctx.GetArgument<OctoObjectId>(Statics.RtIdArg);
            var name = ctx.GetArgument<string>("name");
            var formula = ctx.GetArgument<string>("formula");
            var resultType = ctx.GetArgument<FormulaResultType>("resultType");
            var indexed = ctx.GetArgument<bool?>("indexed") ?? true;
            _logger.LogDebug("Add computed column '{Column}' requested for archive {ArchiveRtId}", name, archiveRtId);

            var gql = (GraphQlUserContext)ctx.UserContext;
            if (gql.User?.IsInRole(CommonConstants.StreamDataAdminRole) != true)
            {
                ctx.Errors.Add(new ExecutionError(
                    $"Adding a computed column requires the '{CommonConstants.StreamDataAdminRole}' role.")
                {
                    Code = Statics.GraphQlForbidden,
                });
                return null;
            }

            var lifecycle = gql.TenantContext.GetArchiveLifecycleService()
                ?? throw AssetRepositoryException.StreamDataNotAvailable();
            var store = gql.TenantContext.GetArchiveRuntimeStore();

            await lifecycle.AddComputedColumnAsync(archiveRtId, name, formula, resultType, indexed);

            var snapshot = await store.GetAsync(archiveRtId)
                ?? throw new ArchiveNotFoundException(archiveRtId);
            return new ArchiveTransitionResultDto(archiveRtId, snapshot.Status, "AddComputedColumn");
        }
        catch (Exception e)
        {
            return ctx.HandleException(e);
        }
    }

    private async Task<object?> ResolveRemoveComputedColumnAsync(IResolveFieldContext<object?> ctx)
    {
        try
        {
            var archiveRtId = ctx.GetArgument<OctoObjectId>(Statics.RtIdArg);
            var name = ctx.GetArgument<string>("name");
            _logger.LogDebug("Remove computed column '{Column}' requested for archive {ArchiveRtId}", name, archiveRtId);

            var gql = (GraphQlUserContext)ctx.UserContext;
            if (gql.User?.IsInRole(CommonConstants.StreamDataAdminRole) != true)
            {
                ctx.Errors.Add(new ExecutionError(
                    $"Removing a computed column requires the '{CommonConstants.StreamDataAdminRole}' role.")
                {
                    Code = Statics.GraphQlForbidden,
                });
                return null;
            }

            var lifecycle = gql.TenantContext.GetArchiveLifecycleService()
                ?? throw AssetRepositoryException.StreamDataNotAvailable();
            var store = gql.TenantContext.GetArchiveRuntimeStore();

            await lifecycle.RemoveComputedColumnAsync(archiveRtId, name);

            var snapshot = await store.GetAsync(archiveRtId)
                ?? throw new ArchiveNotFoundException(archiveRtId);
            return new ArchiveTransitionResultDto(archiveRtId, snapshot.Status, "RemoveComputedColumn");
        }
        catch (Exception e)
        {
            return ctx.HandleException(e);
        }
    }

    private async Task<object?> ResolveUpdateComputedColumnFormulaAsync(IResolveFieldContext<object?> ctx)
    {
        try
        {
            var archiveRtId = ctx.GetArgument<OctoObjectId>(Statics.RtIdArg);
            var name = ctx.GetArgument<string>("name");
            var formula = ctx.GetArgument<string>("formula");
            _logger.LogDebug("Update computed column '{Column}' formula requested for archive {ArchiveRtId}",
                name, archiveRtId);

            var gql = (GraphQlUserContext)ctx.UserContext;
            if (gql.User?.IsInRole(CommonConstants.StreamDataAdminRole) != true)
            {
                ctx.Errors.Add(new ExecutionError(
                    $"Changing a computed column formula requires the '{CommonConstants.StreamDataAdminRole}' role.")
                {
                    Code = Statics.GraphQlForbidden,
                });
                return null;
            }

            var lifecycle = gql.TenantContext.GetArchiveLifecycleService()
                ?? throw AssetRepositoryException.StreamDataNotAvailable();
            var store = gql.TenantContext.GetArchiveRuntimeStore();

            await lifecycle.UpdateComputedColumnFormulaAsync(archiveRtId, name, formula);

            var snapshot = await store.GetAsync(archiveRtId)
                ?? throw new ArchiveNotFoundException(archiveRtId);
            return new ArchiveTransitionResultDto(archiveRtId, snapshot.Status, "UpdateComputedColumnFormula");
        }
        catch (Exception e)
        {
            return ctx.HandleException(e);
        }
    }

    private async Task<object?> ResolveDeleteAsync(IResolveFieldContext<object?> ctx)
    {
        try
        {
            var archiveRtId = ctx.GetArgument<OctoObjectId>(Statics.RtIdArg);
            _logger.LogDebug("Archive Delete requested for {ArchiveRtId}", archiveRtId);

            var gql = (GraphQlUserContext)ctx.UserContext;
            if (gql.User?.IsInRole(CommonConstants.StreamDataAdminRole) != true)
            {
                ctx.Errors.Add(new ExecutionError(
                    $"Archive Delete requires the '{CommonConstants.StreamDataAdminRole}' role.")
                {
                    Code = Statics.GraphQlForbidden,
                });
                return null;
            }

            var lifecycle = gql.TenantContext.GetArchiveLifecycleService()
                ?? throw AssetRepositoryException.StreamDataNotAvailable();

            await lifecycle.DeleteAsync(archiveRtId);
            return true;
        }
        catch (Exception e)
        {
            return ctx.HandleException(e);
        }
    }

    private async Task<object?> ResolveTransitionAsync(
        IResolveFieldContext<object?> ctx,
        string transitionName,
        Func<IArchiveLifecycleService, OctoObjectId, Task> transition)
    {
        try
        {
            var archiveRtId = ctx.GetArgument<OctoObjectId>(Statics.RtIdArg);
            _logger.LogDebug("Archive {Transition} requested for {ArchiveRtId}", transitionName, archiveRtId);

            var gql = (GraphQlUserContext)ctx.UserContext;
            // Concept §5 / T22: archive lifecycle mutations require the StreamDataAdmin role.
            // Per-mutation field-level enforcement on top of the AspNetCore policy on the GraphQL
            // endpoint (which only verifies authentication, not the specific role).
            if (gql.User?.IsInRole(CommonConstants.StreamDataAdminRole) != true)
            {
                ctx.Errors.Add(new ExecutionError(
                    $"Archive {transitionName} requires the '{CommonConstants.StreamDataAdminRole}' role.")
                {
                    Code = Statics.GraphQlForbidden,
                });
                return null;
            }

            var lifecycle = gql.TenantContext.GetArchiveLifecycleService()
                ?? throw AssetRepositoryException.StreamDataNotAvailable();
            var store = gql.TenantContext.GetArchiveRuntimeStore();

            await transition(lifecycle, archiveRtId);

            var snapshot = await store.GetAsync(archiveRtId)
                ?? throw new ArchiveNotFoundException(archiveRtId);

            return new ArchiveTransitionResultDto(archiveRtId, snapshot.Status, transitionName);
        }
        catch (Exception e)
        {
            return ctx.HandleException(e);
        }
    }

    private async Task<object?> ResolveCreateTimeRangeAsync(IResolveFieldContext<object?> ctx)
    {
        try
        {
            var input = ctx.GetArgument<CreateTimeRangeArchiveInputDto>("input");
            _logger.LogDebug(
                "TimeRangeArchive Create requested for target {TargetCkTypeId} ({ColumnCount} columns)",
                input.TargetCkTypeId, input.Columns.Count);

            var gql = (GraphQlUserContext)ctx.UserContext;
            // Same role guard as createRollupArchive: schema-defining ops are admin-only.
            if (gql.User?.IsInRole(CommonConstants.StreamDataAdminRole) != true)
            {
                ctx.Errors.Add(new ExecutionError(
                    $"TimeRangeArchive Create requires the '{CommonConstants.StreamDataAdminRole}' role.")
                {
                    Code = Statics.GraphQlForbidden,
                });
                return null;
            }

            var store = gql.TenantContext.GetTimeRangeArchiveRuntimeStore()
                ?? throw AssetRepositoryException.StreamDataNotAvailable();

            var columns = input.Columns
                .Select(c => new CkArchiveColumnSpec(c.Path, c.Indexed, c.Required))
                .ToList();

            var rtId = await store.InsertAsync(
                input.RtWellKnownName,
                new RtCkId<CkTypeId>(input.TargetCkTypeId),
                columns,
                input.PeriodMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null);

            return rtId;
        }
        catch (Exception e)
        {
            return ctx.HandleException(e);
        }
    }

    private async Task<object?> ResolveCreateRollupAsync(IResolveFieldContext<object?> ctx)
    {
        try
        {
            var input = ctx.GetArgument<CreateRollupArchiveInputDto>("input");
            _logger.LogDebug(
                "Rollup Create requested for source {SourceRtId} ({AggregationCount} aggregations)",
                input.SourceArchiveRtId, input.Aggregations.Count);

            var gql = (GraphQlUserContext)ctx.UserContext;
            // Same role guard as the other rollup mutations.
            if (gql.User?.IsInRole(CommonConstants.StreamDataAdminRole) != true)
            {
                ctx.Errors.Add(new ExecutionError(
                    $"Rollup Create requires the '{CommonConstants.StreamDataAdminRole}' role.")
                {
                    Code = Statics.GraphQlForbidden,
                });
                return null;
            }

            var lifecycle = gql.TenantContext.GetRollupArchiveLifecycleService()
                ?? throw AssetRepositoryException.StreamDataNotAvailable();

            var aggregations = input.Aggregations
                .Select(a => new CkRollupAggregationSpec(a.SourcePath, a.Function, a.TargetColumnName))
                .ToList();

            var rtId = await lifecycle.CreateAsync(
                input.RtWellKnownName,
                input.SourceArchiveRtId,
                TimeSpan.FromMilliseconds(input.BucketSizeMs),
                TimeSpan.FromMilliseconds(input.WatermarkLagMs),
                aggregations);

            return rtId;
        }
        catch (Exception e)
        {
            return ctx.HandleException(e);
        }
    }

    private async Task<object?> ResolveRollupAsync(
        IResolveFieldContext<object?> ctx,
        string mutationName,
        Func<IRollupArchiveLifecycleService, OctoObjectId, Task> mutation)
    {
        try
        {
            var rollupRtId = ctx.GetArgument<OctoObjectId>(Statics.RtIdArg);
            _logger.LogDebug("Rollup {Mutation} requested for {RollupRtId}", mutationName, rollupRtId);

            var gql = (GraphQlUserContext)ctx.UserContext;
            // Rollup mutations are admin-grade — same role guard as the archive lifecycle ones.
            if (gql.User?.IsInRole(CommonConstants.StreamDataAdminRole) != true)
            {
                ctx.Errors.Add(new ExecutionError(
                    $"Rollup {mutationName} requires the '{CommonConstants.StreamDataAdminRole}' role.")
                {
                    Code = Statics.GraphQlForbidden,
                });
                return null;
            }

            var lifecycle = gql.TenantContext.GetRollupArchiveLifecycleService()
                ?? throw AssetRepositoryException.StreamDataNotAvailable();
            var store = gql.TenantContext.GetRollupArchiveRuntimeStore()
                ?? throw AssetRepositoryException.StreamDataNotAvailable();

            await mutation(lifecycle, rollupRtId);

            var snapshot = await store.GetAsync(rollupRtId)
                ?? throw new ArchiveNotFoundException(rollupRtId);

            return new ArchiveTransitionResultDto(rollupRtId, snapshot.Status, mutationName);
        }
        catch (Exception e)
        {
            return ctx.HandleException(e);
        }
    }
}
