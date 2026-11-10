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
///         does the operation, with the same engine services as the mutation, so the effect is the same. Before that, every
///         handler refuses a caller token of another tenant (<see cref="ArchiveMethodCallerTenantGuard" />).
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
            SystemStreamDataCkIds.RollupArchiveCreateArchiveMethodId, "createRollupArchive")
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

        services.TryAddSingleton<ArchiveMethodCallerTenantGuard>();
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
///     against its CK cache (AB#8598). Idempotent; the embedded-import guard keeps a newer version that is already installed.
/// </summary>
internal sealed class SystemTenantStreamDataModelImport(
    ISystemContext systemContext,
    ILogger<SystemTenantStreamDataModelImport> logger) : IAsyncInitializationService
{
    /// <summary>After the default configuration (10), before the method registration validation (15).</summary>
    public int Order => 12;

    public async Task InitializeAsync()
    {
        var tenant = await systemContext.FindTenantContextAsync(systemContext.TenantId).ConfigureAwait(false);
        await tenant.ImportCkModelWithDowngradeGuardAsync(SystemStreamDataCkIds.CkModelId).ConfigureAwait(false);
        logger.LogInformation(
            "System.StreamData ({CkModelId}) is present in the system tenant for the archive CK method handlers",
            SystemStreamDataCkIds.CkModelId);
    }
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
