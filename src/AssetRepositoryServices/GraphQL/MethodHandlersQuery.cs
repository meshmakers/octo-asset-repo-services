using System.Reflection;
using System.Security.Claims;
using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.MethodHandlers;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Security;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Methods;
using Meshmakers.Octo.Services.Methods;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     Diagnostics of the CK method handler registry (AB#7837, F3.2). Mounted as <c>MethodHandlers</c> on the root
///     query; the root field enforces <see cref="SystemAdministratorGuard" /> (<see cref="Resolve" />): a token of the
///     system tenant, and for a user token the role <c>AdminPanelManagement</c>. Everyone else gets null and the error
///     code <see cref="Statics.GraphQlMethodHandlersForbidden" />. The registry is platform-wide: it names services,
///     instance ids, versions and the tenants a handler serves. Every gateway replica answers from its own memory.
///     Returns service data only, never parameters, tokens or user data.
/// </summary>
[DoNotRegister]
internal sealed class MethodHandlersQuery : ObjectGraphType<MethodHandlersRequestContext>
{
    public MethodHandlersQuery()
    {
        Name = "MethodHandlersQuery";
        Description =
            "Diagnostics of the CK method handler registry (system administrators only). Service data only.";

        Field<NonNullGraphType<MethodHandlersAnsweredByGraphType>>("answeredBy")
            .Description("The gateway replica that answered, with its snapshot health.")
            .ResolveAsync(async ctx => (await ctx.Source.GetViewAsync(ctx.CancellationToken)).AnsweredBy);

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<MethodHandlerMethodGraphType>>>>("methods")
            .Description("CK methods with their handler instances.")
            .Argument<StringGraphType>("search", "Matches the method id or the service name of an instance (case-insensitive).")
            .Argument<ListGraphType<NonNullGraphType<MethodHandlerMethodStateGraphType>>>("states",
                "Only methods in one of these states.")
            .ResolveAsync(async ctx =>
            {
                var view = await ctx.Source.GetViewAsync(ctx.CancellationToken);
                return view.Filter(ctx.GetArgument<string?>("search"),
                    ctx.GetArgument<List<MethodHandlerMethodState>?>("states"));
            });
    }

    /// <summary>Mounts the <c>MethodHandlers</c> root field on the query type.</summary>
    internal static void AddTo(ObjectGraphType query)
    {
        query.Field("MethodHandlers", new MethodHandlersQuery())
            .Description("Diagnostics of the CK method handler registry of the answering gateway replica: per method " +
                         "the handler instances, their state, last seen time, and heartbeat versus persisted snapshot. " +
                         "Requires a token of the system tenant (a user token also the AdminPanelManagement role). " +
                         "Service data only.")
            .Resolve(Resolve);
    }

    /// <summary>
    ///     Resolver of the root field: the system administrator check for the whole query. Without it the field is
    ///     null with error code <see cref="Statics.GraphQlMethodHandlersForbidden" /> and no handler data is read.
    /// </summary>
    internal static object? Resolve(IResolveFieldContext<object?> ctx)
    {
        var gql = (GraphQlUserContext)ctx.UserContext;
        var services = ctx.RequestServices!;
        var systemContext = services.GetRequiredService<ISystemContext>();
        if (gql.User == null || !SystemAdministratorGuard.IsSystemAdministrator(gql.User, systemContext))
        {
            ctx.Errors.Add(new ExecutionError(
                "The method handler diagnostics are available to system administrators only.")
            {
                Code = Statics.GraphQlMethodHandlersForbidden
            });
            return null;
        }

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger<MethodHandlersQuery>();
        return new MethodHandlersRequestContext(
            services.GetRequiredService<ICkMethodHandlerRegistryDiagnostics>(),
            services.GetService<ICkMethodHandlerSnapshotStore>(),
            services.GetRequiredService<ICkCacheService>(),
            systemContext.TenantId,
            logger);
    }
}

/// <summary>Per-request state of the <c>MethodHandlers</c> query: builds the view once for all selected fields.</summary>
internal sealed class MethodHandlersRequestContext(
    ICkMethodHandlerRegistryDiagnostics diagnostics,
    ICkMethodHandlerSnapshotStore? snapshotStore,
    ICkCacheService ckCacheService,
    string systemTenantId,
    ILogger logger)
{
    private static readonly string ServiceVersion = ReadServiceVersion();
    private Task<MethodHandlersView>? _view;

    internal Task<MethodHandlersView> GetViewAsync(CancellationToken cancellationToken)
    {
        return _view ??= MethodHandlersViewBuilder.BuildAsync(diagnostics, snapshotStore, ckCacheService,
            systemTenantId, ServiceVersion, logger, cancellationToken);
    }

    private static string ReadServiceVersion()
    {
        var version = typeof(MethodHandlersQuery).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(version))
        {
            return typeof(MethodHandlersQuery).Assembly.GetName().Version?.ToString() ?? "unknown";
        }

        // Drop the source revision suffix ("1.2.3+abcdef").
        var plus = version.IndexOf('+');
        return plus > 0 ? version[..plus] : version;
    }
}
