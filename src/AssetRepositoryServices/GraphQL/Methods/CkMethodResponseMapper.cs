using System.Text.Json;
using GraphQL;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;

/// <summary>The GraphQL form of a method response: a value for the field, or ONE error (never both).</summary>
internal readonly record struct CkMethodGraphQlOutcome(CkMethodCallResult? Value, ExecutionError? Error);

/// <summary>
///     AB#5670: maps a <see cref="CkMethodResponse" /> of the gateway to what the generated method field answers:
///     the typed result for a success, ONE GraphQL error with <c>extensions.code</c> and <c>extensions.parameters</c>
///     for every failed response (platform codes and codes declared by the handler alike, through
///     <see cref="CkMethodGraphQlError" />). The result type carries no error member.
///     AB#7665 (F-3): a client only sees a code the platform issued. The allow-list is the one of the handler host: the
///     platform catalog (<see cref="CkMethodErrorCodes.Catalog" />) and the codes the method declares pass; any other code
///     (an unknown <c>METHOD_</c> code or an undeclared one) becomes <c>METHOD_HANDLER_FAILED</c> with the correlation id,
///     and the original code is logged, never returned.
/// </summary>
internal static class CkMethodResponseMapper
{
    public static CkMethodGraphQlOutcome Map(CkMethodResponse response, CkMethodGraph method,
        ICkCacheService ckCacheService, string tenantId, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(method);

        if (!response.Success)
        {
            var error = response.Error ?? CkMethodError.Create(CkMethodErrorCodes.MethodHandlerFailed,
                (CkMethodErrorParameters.MethodId, method.QualifiedMethodId),
                (CkMethodErrorParameters.CorrelationId, response.InvocationId.ToString("D")));
            return new CkMethodGraphQlOutcome(null, CkMethodGraphQlError.Create(
                Allowed(error.Code, method) ? error : HandlerFailed(response, method, error.Code, logger)));
        }

        // An accepted asynchronous call (F3.6) has no result yet: the caller gets the invocation id only.
        if (response.Operation != null || method.Definition.Result == null || string.IsNullOrEmpty(response.ResultJson))
        {
            return new CkMethodGraphQlOutcome(new CkMethodCallResult(response.InvocationId, null), null);
        }

        try
        {
            using var document = JsonDocument.Parse(response.ResultJson);
            var value = CkMethodResultConverter.ToResultValue(ckCacheService, tenantId, method.Definition.Result,
                document.RootElement);
            return new CkMethodGraphQlOutcome(new CkMethodCallResult(response.InvocationId, value), null);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            // The handler answered with a result that does not fit its own declaration: a handler failure, with the
            // correlation id for the handler log. Nothing of the result leaves the service.
            return new CkMethodGraphQlOutcome(null, CkMethodGraphQlError.Create(CkMethodError.Create(
                CkMethodErrorCodes.MethodHandlerFailed,
                (CkMethodErrorParameters.MethodId, method.QualifiedMethodId),
                (CkMethodErrorParameters.CorrelationId, response.InvocationId.ToString("D")))));
        }
    }

    private static bool Allowed(string code, CkMethodGraph method) =>
        CkMethodErrorCodes.All.Contains(code, StringComparer.Ordinal) ||
        (method.Definition.Errors?.Any(e => string.Equals(e.Code, code, StringComparison.Ordinal)) ?? false);

    private static CkMethodError HandlerFailed(CkMethodResponse response, CkMethodGraph method, string? original,
        ILogger? logger)
    {
        // the code is data of a foreign responder: bounded in the log, never on the wire
        var logged = original is { Length: > 64 } ? original[..64] + "..." : original;
        logger?.LogError(
            "CK method {MethodId} invocation {InvocationId}: the responder answered with error code {Code}, which is neither a platform code nor declared by the method; answered METHOD_HANDLER_FAILED",
            method.QualifiedMethodId, response.InvocationId, logged);
        return CkMethodError.Create(CkMethodErrorCodes.MethodHandlerFailed,
            (CkMethodErrorParameters.MethodId, method.QualifiedMethodId),
            (CkMethodErrorParameters.CorrelationId, response.InvocationId.ToString("D")));
    }
}
