using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

/// <summary>
///     The coded error of a runtime-entity query that MongoDB stopped at its time limit
///     (<see cref="RuntimeQueryTimeoutException" />, configuration <c>RuntimeQuery:MaxTimeMs</c>): GraphQL code
///     <see cref="Statics.GraphQlRuntimeQueryTimeout" /> (<c>ASSET1008</c>), REST <c>504</c> with problem details. Every
///     occurrence is logged once as a warning with tenant, CK type, duration, limit and the error id the client receives.
/// </summary>
internal static class RuntimeQueryTimeoutError
{
    /// <summary>Extension key of the error id.</summary>
    public const string ErrorIdKey = "errorId";

    /// <summary>The problem <c>type</c> URI of the REST answer; stable, not dereferenced.</summary>
    public const string ProblemType = "https://schemas.meshmakers.cloud/errors/runtime-query-timeout";

    /// <summary>The log category of the warning.</summary>
    public const string LogCategory = "Meshmakers.Octo.Backend.AssetRepositoryServices.RuntimeQueryTimeout";

    /// <summary>
    ///     Finds a <see cref="RuntimeQueryTimeoutException" /> in <paramref name="exception" /> or its inner exceptions.
    /// </summary>
    public static bool TryFind(Exception? exception, [NotNullWhen(true)] out RuntimeQueryTimeoutException? timeout)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is RuntimeQueryTimeoutException found)
            {
                timeout = found;
                return true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (TryFind(inner, out timeout))
                    {
                        return true;
                    }
                }
            }
        }

        timeout = null;
        return false;
    }

    /// <summary>
    ///     Logs the time-out as a warning and returns its GraphQL error.
    /// </summary>
    public static ExecutionError CreateGraphQlError(RuntimeQueryTimeoutException timeout, ILogger? logger)
    {
        var errorId = LogTimeout(timeout, logger);
        return new ExecutionError(CreateMessage(timeout, errorId), timeout)
        {
            Code = Statics.GraphQlRuntimeQueryTimeout,
            Extensions = new Dictionary<string, object?>
            {
                [CkMethodGraphQlError.CodeKey] = Statics.GraphQlRuntimeQueryTimeout,
                [ErrorIdKey] = errorId,
                [CkMethodGraphQlError.ParametersKey] = CreateParameters(timeout)
            }
        };
    }

    /// <summary>
    ///     Logs the time-out as a warning and returns its REST problem details (status <c>504</c>).
    /// </summary>
    public static ProblemDetails CreateProblem(RuntimeQueryTimeoutException timeout, ILogger? logger)
    {
        var errorId = LogTimeout(timeout, logger);
        var problem = new ProblemDetails
        {
            Type = ProblemType,
            Title = "Runtime query exceeded its time limit",
            Status = StatusCodes.Status504GatewayTimeout,
            Detail = CreateMessage(timeout, errorId)
        };
        problem.Extensions["code"] = Statics.GraphQlRuntimeQueryTimeout;
        problem.Extensions[ErrorIdKey] = errorId;
        foreach (var (key, value) in CreateParameters(timeout))
        {
            problem.Extensions[key] = value;
        }

        return problem;
    }

    private static string LogTimeout(RuntimeQueryTimeoutException timeout, ILogger? logger)
    {
        var errorId = Guid.NewGuid().ToString("N");
        logger?.LogWarning(
            "Runtime query timed out: CK type {CkTypeId} in tenant {TenantId} ran {DurationMs} ms and was stopped at " +
            "its time limit of {MaxTimeMs} ms, error id {ErrorId}",
            timeout.CkTypeId, timeout.TenantId, (long)timeout.Elapsed.TotalMilliseconds,
            (long)timeout.MaxTime.TotalMilliseconds, errorId);
        return errorId;
    }

    private static string CreateMessage(RuntimeQueryTimeoutException timeout, string errorId) =>
        string.Format(CultureInfo.InvariantCulture, "{0} Error id {1}.", timeout.Message, errorId);

    private static Dictionary<string, object?> CreateParameters(RuntimeQueryTimeoutException timeout) => new()
    {
        ["ckTypeId"] = timeout.CkTypeId,
        ["maxTimeMs"] = (long)timeout.MaxTime.TotalMilliseconds,
        ["elapsedMs"] = (long)timeout.Elapsed.TotalMilliseconds
    };
}
