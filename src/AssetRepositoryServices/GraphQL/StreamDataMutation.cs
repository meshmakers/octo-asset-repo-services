using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
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
        Description = "Archive lifecycle mutations: activate, disable, enable, retry activation.";

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

            var lifecycle = ctx.RequestServices?.GetRequiredService<IArchiveLifecycleService>()
                ?? throw AssetRepositoryException.ServiceNotRegistered(typeof(IArchiveLifecycleService));
            var store = ctx.RequestServices?.GetRequiredService<ICkArchiveRuntimeStore>()
                ?? throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkArchiveRuntimeStore));

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
}
