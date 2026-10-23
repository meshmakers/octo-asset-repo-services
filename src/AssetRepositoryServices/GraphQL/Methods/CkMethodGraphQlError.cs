using GraphQL;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;

/// <summary>
///     The GraphQL form of a failed CK method response (AB#6912): ONE error whose <c>extensions</c> carry the
///     machine-readable contract (<c>code</c>, <c>parameters</c>). The message is the fixed, language-neutral text
///     below, so two calls that fail with the same code and parameters (a refused call on an existing and on a missing
///     target) are byte-identical for the client.
/// </summary>
internal static class CkMethodGraphQlError
{
    /// <summary>Extension member with the error code.</summary>
    public const string CodeKey = "code";

    /// <summary>Extension member with the language-neutral error parameters.</summary>
    public const string ParametersKey = "parameters";

    /// <summary>Extension member with the per-item details (AB#5670), present only when the response carries some.</summary>
    public const string DetailsKey = "details";

    public static ExecutionError Create(CkMethodError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var extensions = new Dictionary<string, object?>
        {
            [CodeKey] = error.Code,
            [ParametersKey] = Parameters(error.Parameters)
        };
        if (error.Details is { Count: > 0 })
        {
            extensions[DetailsKey] = error.Details.Select(d => (object)new Dictionary<string, object?>
            {
                [CodeKey] = d.Code,
                [ParametersKey] = Parameters(d.Parameters)
            }).ToList();
        }

        return new ExecutionError($"The CK method call failed: {error.Code}")
        {
            Code = error.Code,
            Extensions = extensions
        };
    }

    private static Dictionary<string, object?> Parameters(IReadOnlyDictionary<string, string> parameters) =>
        parameters.OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal);
}
