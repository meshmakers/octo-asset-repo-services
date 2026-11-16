using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

/// <summary>
///     REST side of the runtime query time-out: a controller action whose runtime query MongoDB stopped at its time limit
///     answers <c>504</c> with problem details (<c>application/problem+json</c>) carrying the code <c>ASSET1008</c>, the
///     error id and the CK type, instead of a generic 500.
/// </summary>
internal sealed class RuntimeQueryTimeoutExceptionFilter(ILoggerFactory loggerFactory) : IExceptionFilter
{
    private readonly ILogger _logger = loggerFactory.CreateLogger(RuntimeQueryTimeoutError.LogCategory);

    /// <inheritdoc />
    public void OnException(ExceptionContext context)
    {
        if (!RuntimeQueryTimeoutError.TryFind(context.Exception, out var timeout))
        {
            return;
        }

        context.Result = new ObjectResult(RuntimeQueryTimeoutError.CreateProblem(timeout, _logger))
        {
            StatusCode = StatusCodes.Status504GatewayTimeout,
            ContentTypes = { "application/problem+json" }
        };
        context.ExceptionHandled = true;
    }
}
