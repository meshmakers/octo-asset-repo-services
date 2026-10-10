using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.BlueprintLock;

/// <summary>
///     REST side of the blueprint-lock error contract (AB#6385): a controller action that raises the refusal of the engine
///     write guard (AB#6384) answers with <c>403 Forbidden</c> and RFC 9457 problem details
///     (<c>application/problem+json</c>) carrying the same stable <c>code</c> as GraphQL.
/// </summary>
internal sealed class BlueprintLockedExceptionFilter : IExceptionFilter
{
    /// <summary>The problem <c>type</c> URI; stable, not dereferenced.</summary>
    public const string ProblemType = "https://schemas.meshmakers.cloud/errors/blueprint-locked";

    /// <inheritdoc />
    public void OnException(ExceptionContext context)
    {
        var offenders = GetOffenders(context.Exception);
        if (offenders == null)
        {
            return;
        }

        context.Result = new ObjectResult(CreateProblem(offenders))
        {
            StatusCode = StatusCodes.Status403Forbidden,
            ContentTypes = { "application/problem+json" }
        };
        context.ExceptionHandled = true;
    }

    /// <summary>
    ///     Builds the problem details of a blueprint-lock refusal: the stable <c>code</c> and <c>messageNumber</c>, the
    ///     first offender's <c>ckTypeId</c>, <c>rtId</c> and <c>reason</c>, and <c>items</c> for every refused entity.
    /// </summary>
    internal static ProblemDetails CreateProblem(IReadOnlyList<BlueprintLockOffender> offenders)
    {
        var first = offenders[0];
        var problem = new ProblemDetails
        {
            Type = ProblemType,
            Title = "Entity is locked by a blueprint",
            Status = StatusCodes.Status403Forbidden,
            Detail = offenders.Count == 1
                ? first.Message
                : $"Access denied: {offenders.Count} entities are locked by blueprint and cannot be changed by users. " +
                  "Nothing was written."
        };
        problem.Extensions["code"] = BlueprintLockError.Code;
        problem.Extensions["messageNumber"] = BlueprintLockError.MessageNumber;
        problem.Extensions["ckTypeId"] = first.CkTypeId;
        problem.Extensions["rtId"] = first.RtId;
        problem.Extensions["reason"] = first.Reason.ToString();
        problem.Extensions["items"] = offenders
            .Select(o => new { ckTypeId = o.CkTypeId, rtId = o.RtId, reason = o.Reason.ToString() })
            .ToList();
        return problem;
    }

    private static IReadOnlyList<BlueprintLockOffender>? GetOffenders(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case BlueprintLockedException locked:
                    return locked.Offenders;
                case RuntimeRepositoryException repositoryException:
                    var offenders = BlueprintLockError.GetOffenders(repositoryException.OperationResult.Messages);
                    if (offenders.Count > 0)
                    {
                        return offenders;
                    }

                    break;
                case ExchangeException { MessageNumber: BlueprintLockError.MessageNumber } exchangeException:
                    var imported = BlueprintLockError.ParseImportOffenders(exchangeException.Message);
                    return imported.Count > 0
                        ? imported
                        : [new BlueprintLockOffender(string.Empty, null, BlueprintLockReason.EntityLocked,
                            exchangeException.Message)];
            }
        }

        return null;
    }
}
