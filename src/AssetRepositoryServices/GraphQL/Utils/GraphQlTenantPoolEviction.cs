using GraphQL;
using GraphQL.Execution;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

/// <summary>
///     AB#8228 — GraphQL turns an exception of a resolver into an entry of the response's <c>errors</c>, so the HTTP
///     pipeline (and with it the eviction middleware) never sees it. When the tenant's MongoDB user was dropped and
///     re-created, the pooled connections of this process keep failing with error 13 until a restart. This hook
///     evicts the tenant's cached clients on such an authentication failure so the next request authenticates anew.
/// </summary>
/// <remarks>
///     Round 2: nearly every resolver catches its exception and maps it itself through
///     <see cref="ResolveConnectionContextExtensions.HandleException" />, so <c>UnhandledExceptionDelegate</c> never
///     runs for them (it only sees exceptions a resolver does not catch). The hook is therefore called from
///     <c>HandleException</c> (<see cref="EvictInBackground" />) and still from the unhandled-exception delegate.
/// </remarks>
internal static class GraphQlTenantPoolEviction
{
    /// <summary>
    ///     Evicts the tenant's cached MongoDB clients when <paramref name="exception" /> is an authentication failure.
    ///     Rate-limited per tenant by <paramref name="healer" />; never throws.
    /// </summary>
    public static async Task<bool> TryEvictAsync(Exception exception, object? userContext,
        ISystemContext? systemContext, ILogger? logger, TenantDatabaseUserHealer? healer = null)
    {
        try
        {
            if (systemContext == null || userContext is not GraphQlUserContext gql ||
                !TenantDatabaseUserHealer.IsAuthenticationFailure(exception))
            {
                return false;
            }

            return await (healer ?? TenantDatabaseUserHealer.Default)
                .EvictOnAuthenticationFailureAsync(systemContext, gql.TenantId,
                    logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, exception)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not evict the cached MongoDB clients after an authentication failure");
            return false;
        }
    }

    /// <summary>Convenience overload for the GraphQL.NET unhandled-exception hook (services of the request).</summary>
    public static Task<bool> TryEvictAsync(UnhandledExceptionContext context, ILogger? logger = null) =>
        TryEvictAsync(context.OriginalException, context.Context?.UserContext,
            context.Context?.RequestServices?.GetService(typeof(ISystemContext)) as ISystemContext,
            logger ?? CreateLogger(context.Context?.RequestServices));

    /// <summary>
    ///     Hook of <c>HandleException</c>: evicts in the background (the resolver answers at once, the response carries
    ///     the error as before). Resolves everything from the request scope of <paramref name="context" />. Never
    ///     throws; the returned task completes when the eviction is done (tests await it).
    /// </summary>
    public static Task<bool> EvictInBackground(IResolveFieldContext context, Exception exception,
        TenantDatabaseUserHealer? healer = null)
    {
        try
        {
            if (!TenantDatabaseUserHealer.IsAuthenticationFailure(exception))
            {
                return Task.FromResult(false);
            }

            return TryEvictAsync(exception, context.UserContext,
                context.RequestServices?.GetService(typeof(ISystemContext)) as ISystemContext,
                CreateLogger(context.RequestServices), healer);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private static ILogger? CreateLogger(IServiceProvider? services) =>
        (services?.GetService(typeof(ILoggerFactory)) as ILoggerFactory)?.CreateLogger(
            typeof(GraphQlTenantPoolEviction).FullName!);
}
