using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Methods;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;

/// <summary>One CK method of System.StreamData that the asset repository handles in-process (<c>handler: service</c>).</summary>
/// <param name="MethodId">Qualified method id, always the generated constant of <c>SystemStreamDataCkIds</c>.</param>
/// <param name="HandlerType">The handler class.</param>
/// <param name="Mutation">The streamData GraphQL mutation the method replaces (deprecated, still working).</param>
/// <param name="Register">Registers the handler with the in-process host of octo-common-services.</param>
internal sealed record ArchiveMethodHandlerRegistration(
    string MethodId,
    Type HandlerType,
    string Mutation,
    Action<ICkMethodHandlerRegistrationBuilder> Register)
{
    /// <summary>One row of <see cref="ArchiveMethodHost.Handlers" />.</summary>
    public static ArchiveMethodHandlerRegistration For<THandler>(string methodId, string mutation)
        where THandler : class, ICkMethodHandler =>
        new(methodId, typeof(THandler), mutation, b => b.Handle<THandler>(methodId));
}

/// <summary>
///     The archive lifecycle of System.StreamData as CK methods (AB#8598, Feature AB#5697 F4.3): the one place that hosts their
///     handlers in the asset repository, the owner of stream data. Each method replaces one streamData GraphQL mutation, which
///     stays and is deprecated with the method id as reason.
/// </summary>
/// <remarks>
///     <para>
///         The handlers run in-process (<c>AddCkInProcessMethodHandlers</c>): the gateway of this service calls them without a
///         bus hop, through the invocation pipeline that enforces the declared authorization (role <c>StreamDataAdmin</c>, scope
///         <c>octo_api</c>, tenant of the token) and maps a declared failure to a GraphQL error with its code. A handler only
///         does the operation, with the same engine services as the mutation, so the effect is the same. A caller token of another
///         tenant never reaches a handler: the in-process transport of octo-common-services refuses it centrally
///         (<c>CkMethodCallerTenantFilter</c>, AB#8711, <c>METHOD_FORBIDDEN</c> with reason <c>tenant-not-allowed</c>).
///     </para>
///     <para>
///         The startup validation of the in-process registrations looks the methods up in the CK cache of the system tenant. That
///         tenant holds System.StreamData only when stream data was enabled for it, so <see cref="SystemTenantStreamDataModelImport" />
///         imports the embedded model there first (Order 12, after the default configuration of order 10). The import is
///         idempotent and never downgrades (embedded-import guard). Switch the host off with
///         <c>Methods:StreamData:Enabled=false</c> (<c>OCTO_METHODS__STREAMDATA__ENABLED</c>): no handler, no import; the
///         mutations keep working, a method call then answers <c>METHOD_HANDLER_NOT_AVAILABLE</c>.
///     </para>
/// </remarks>
internal static class ArchiveMethodHost
{
    /// <summary>All handlers, with the mutation each one replaces (AB#8598).</summary>
    public static IReadOnlyList<ArchiveMethodHandlerRegistration> Handlers { get; } =
    [
        ArchiveMethodHandlerRegistration.For<ArchiveActivateHandler>(SystemStreamDataCkIds.ArchiveActivateMethodId,
            "activateArchive"),
        ArchiveMethodHandlerRegistration.For<ArchiveDisableHandler>(SystemStreamDataCkIds.ArchiveDisableMethodId,
            "disableArchive"),
        ArchiveMethodHandlerRegistration.For<ArchiveEnableHandler>(SystemStreamDataCkIds.ArchiveEnableMethodId,
            "enableArchive"),
        ArchiveMethodHandlerRegistration.For<ArchiveRetryActivationHandler>(
            SystemStreamDataCkIds.ArchiveRetryActivationMethodId, "retryArchiveActivation"),
        ArchiveMethodHandlerRegistration.For<ArchiveDeleteArchiveHandler>(SystemStreamDataCkIds.ArchiveDeleteArchiveMethodId,
            "deleteArchive"),
        ArchiveMethodHandlerRegistration.For<TimeRangeArchiveCreateArchiveHandler>(
            SystemStreamDataCkIds.TimeRangeArchiveCreateArchiveMethodId, "createTimeRangeArchive"),
        ArchiveMethodHandlerRegistration.For<RollupArchiveCreateArchiveHandler>(
            SystemStreamDataCkIds.RollupArchiveCreateArchiveMethodId, "createRollupArchive"),
        // AB#8599 (System.StreamData 1.25.0): rollup control and recomputation
        ArchiveMethodHandlerRegistration.For<RollupArchiveFreezeHandler>(SystemStreamDataCkIds.RollupArchiveFreezeMethodId,
            "freezeRollupArchive"),
        ArchiveMethodHandlerRegistration.For<RollupArchiveUnfreezeHandler>(SystemStreamDataCkIds.RollupArchiveUnfreezeMethodId,
            "unfreezeRollupArchive"),
        ArchiveMethodHandlerRegistration.For<RollupArchiveRewindWatermarkHandler>(
            SystemStreamDataCkIds.RollupArchiveRewindWatermarkMethodId, "rewindRollupWatermark"),
        ArchiveMethodHandlerRegistration.For<RollupArchiveRecomputeHandler>(SystemStreamDataCkIds.RollupArchiveRecomputeMethodId,
            "recomputeArchive"),
        ArchiveMethodHandlerRegistration.For<RollupArchiveBackfillFromSourceHandler>(
            SystemStreamDataCkIds.RollupArchiveBackfillFromSourceMethodId, "backfillRollupFromSource")
    ];

    /// <summary>The deprecation reason of a replaced mutation: <c>Use &lt;method id&gt;</c> (AB#8598).</summary>
    public static string DeprecationReasonOf(string mutation) =>
        $"Use {Handlers.Single(h => h.Mutation == mutation).MethodId}";

    /// <summary>Reads the options (the host registration runs before the service provider exists).</summary>
    public static StreamDataMethodsOptions ReadOptions(IConfiguration configuration) =>
        configuration.GetSection(StreamDataMethodsOptions.SectionName).Get<StreamDataMethodsOptions>() ??
        new StreamDataMethodsOptions();

    /// <summary>
    ///     Registers the in-process handlers and the import of System.StreamData into the system tenant. Nothing is registered
    ///     when <see cref="StreamDataMethodsOptions.Enabled" /> is false.
    /// </summary>
    /// <returns><c>true</c> when the handlers are hosted.</returns>
    public static bool AddArchiveCkMethodHandlers(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StreamDataMethodsOptions>().BindConfiguration(StreamDataMethodsOptions.SectionName);
        if (!ReadOptions(configuration).Enabled)
        {
            return false;
        }

        services.AddCkInProcessMethodHandlers(builder =>
        {
            foreach (var handler in Handlers)
            {
                handler.Register(builder);
            }
        });
        services.AddInitializationService<SystemTenantStreamDataModelImport>();
        return true;
    }
}

/// <summary>
///     Imports the embedded System.StreamData into the system tenant before the in-process method registrations are validated
///     against its CK cache (AB#8598), and leaves that cache loaded with the model (AB#8722). Idempotent; the embedded-import
///     guard keeps a newer version that is already installed.
/// </summary>
/// <remarks>
///     The startup validation (Order 15) reads only a CK cache that is already loaded; it never loads one. The import unloads
///     the cache of the system tenant whenever the model set changed (this replica imported) or the loaded cache lacks the
///     installed version (another replica imported first, AB#8667), and nothing at startup loaded it again before the
///     validation. On the first rollout with several replicas the validation then found no method and stopped the pod
///     (AB#8722, test-2 2026-11-10). This step therefore loads the cache again after the import and checks that it holds
///     System.StreamData; a cache loaded concurrently from the state before the import is replaced. A model that is really
///     not installed (an import skipped for missing dependencies) is logged here and still fails the validation.
/// </remarks>
internal sealed class SystemTenantStreamDataModelImport(
    ISystemContext systemContext,
    ICkCacheService ckCache,
    ILogger<SystemTenantStreamDataModelImport> logger) : IAsyncInitializationService
{
    /// <summary>Loads of the cache after the import before the step gives up and leaves the result to the validation.</summary>
    internal const int MaxCacheLoadAttempts = 3;

    /// <summary>After the default configuration (10), before the method registration validation (15).</summary>
    public int Order => 12;

    public async Task InitializeAsync()
    {
        var modelId = SystemStreamDataCkIds.CkModelId;
        var tenant = await systemContext.FindTenantContextAsync(systemContext.TenantId).ConfigureAwait(false);
        await tenant.ImportCkModelWithDowngradeGuardAsync(modelId).ConfigureAwait(false);

        if (!await tenant.IsCkModelSatisfiedAsync(modelId).ConfigureAwait(false))
        {
            logger.LogWarning(
                "System.StreamData ({CkModelId}) is not installed in the system tenant '{TenantId}' after the import (see the " +
                "import log above); the archive CK method handlers will fail the startup validation",
                modelId, tenant.TenantId);
            return;
        }

        for (var attempt = 1; attempt <= MaxCacheLoadAttempts; attempt++)
        {
            await tenant.LoadCacheForTenantAsync().ConfigureAwait(false);
            if (CacheHolds(tenant.TenantId, modelId))
            {
                logger.LogInformation(
                    "System.StreamData ({CkModelId}) is present in the system tenant for the archive CK method handlers",
                    modelId);
                return;
            }

            if (attempt == MaxCacheLoadAttempts)
            {
                break;
            }

            // loaded from the state before the import (a concurrent load in this process): drop it and load again
            logger.LogInformation(
                "The CK cache of the system tenant '{TenantId}' lacks System.StreamData after the import; reloading it " +
                "(attempt {Attempt} of {MaxAttempts})", tenant.TenantId, attempt, MaxCacheLoadAttempts);
            ckCache.Unload(tenant.TenantId);
        }

        logger.LogWarning(
            "The CK cache of the system tenant '{TenantId}' still lacks System.StreamData ({CkModelId}) after " +
            "{MaxAttempts} loads; the archive CK method handlers will fail the startup validation",
            tenant.TenantId, modelId, MaxCacheLoadAttempts);
    }

    /// <summary>The loaded cache holds the model at the embedded version or a newer one (the guard keeps a newer one).</summary>
    private bool CacheHolds(string tenantId, CkModelId modelId) =>
        ckCache.IsTenantLoaded(tenantId) &&
        ckCache.GetCkModelIds(tenantId).Any(id => id.Name == modelId.Name && id.Version.CompareTo(modelId.Version) >= 0);
}

/// <summary>
///     Settings of the archive CK methods (section <c>Methods:StreamData</c>, environment variable prefix
///     <c>OCTO_METHODS__STREAMDATA__</c>).
/// </summary>
public sealed class StreamDataMethodsOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Methods:StreamData";

    /// <summary>
    ///     Hosts the handlers (default true). Off: no handler and no model import into the system tenant; the gateway answers
    ///     <c>METHOD_HANDLER_NOT_AVAILABLE</c> and the streamData mutations keep working.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
