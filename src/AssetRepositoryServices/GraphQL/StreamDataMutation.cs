using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
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
