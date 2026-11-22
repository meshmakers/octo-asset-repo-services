using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.CkModelValidation;

/// <summary>
///     Refusal of <c>Models/ImportCk</c> for a source draft (a zip of the <c>ConstructionKit/</c> folder) that does not
///     compile: <c>400</c> with problem details (<c>application/problem+json</c>). Nothing was cached, queued or imported.
///     The extensions carry the findings exactly as <c>Models/ValidateCk</c> reports them (<c>errors</c>,
///     <c>warnings</c>: message number, level, file relative to <c>ConstructionKit/</c>, element path, text), plus
///     <c>code</c> and <c>message</c> (read by the SDK as a coded refusal) and <c>fileName</c>. No server path.
/// </summary>
public static class CkModelCompileFailedError
{
    /// <summary>Stable error code of the refusal (problem details extension <c>code</c>).</summary>
    public const string Code = "CK_MODEL_COMPILE_FAILED";

    /// <summary>Problem type of the refusal; stable, not dereferenced.</summary>
    public const string ProblemType = "https://schemas.meshmakers.cloud/errors/ck-model-compile-failed";

    /// <summary>Most findings listed in the English <c>detail</c>; the <c>errors</c> extension always has all.</summary>
    public const int MaxDetailFindings = 10;

    /// <summary>Builds the problem details of a source draft that did not compile.</summary>
    public static ProblemDetails CreateProblem(string fileName, CkModelValidationResponseDto findings)
    {
        var listed = findings.Errors.Take(MaxDetailFindings).Select(Describe).ToList();
        var more = findings.Errors.Count > listed.Count ? $" (+{findings.Errors.Count - listed.Count} more)" : "";
        var message =
            $"The CK model source in {fileName} does not compile: {findings.Errors.Count} error(s): " +
            $"{string.Join("; ", listed)}{more}. Nothing was imported. Fix the draft (validate it with " +
            "Models/ValidateCk) and import it again.";
        var problem = new ProblemDetails
        {
            Type = ProblemType,
            Title = "The CK model source does not compile",
            Status = StatusCodes.Status400BadRequest,
            Detail = message
        };
        problem.Extensions["code"] = Code;
        problem.Extensions["message"] = message;
        problem.Extensions["fileName"] = fileName;
        problem.Extensions["errors"] = findings.Errors;
        problem.Extensions["warnings"] = findings.Warnings;
        return problem;
    }

    /// <summary>The 400 action result with <c>application/problem+json</c>.</summary>
    public static ObjectResult CreateResult(string fileName, CkModelValidationResponseDto findings) =>
        new(CreateProblem(fileName, findings))
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/problem+json" }
        };

    private static string Describe(CkModelValidationMessageDto finding)
    {
        var where = string.Join(" ", new[] { finding.File, finding.ElementPath }.Where(s => !string.IsNullOrEmpty(s)));
        return string.IsNullOrEmpty(where)
            ? $"[{finding.MessageNumber}] {finding.Message}"
            : $"[{finding.MessageNumber}] {where}: {finding.Message}";
    }
}
