using GraphQL.Execution;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

/// <summary>
///     AB#8228 — GraphQL turns an exception of a resolver into an entry of the response's <c>errors</c>, so the HTTP
///     pipeline (and with it the tenant middleware's eviction) never sees it. When the tenant's MongoDB user was
///     dropped and re-created, the pooled connections of this process keep failing with error 13 until a restart.
///     This hook, called from <c>UnhandledExceptionDelegate</c>, evicts the tenant's cached clients on such an
///     authentication failure so the next request authenticates anew.
/// </summary>
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

    /// <summary>Convenience overload for the GraphQL.NET unhandled-exception hook.</summary>
    public static Task<bool> TryEvictAsync(UnhandledExceptionContext context, ILogger? logger) =>
        TryEvictAsync(context.OriginalException, context.Context?.UserContext,
            context.Context?.RequestServices?.GetService(typeof(ISystemContext)) as ISystemContext, logger);
}
