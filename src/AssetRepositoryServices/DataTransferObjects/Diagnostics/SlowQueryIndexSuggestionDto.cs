namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Diagnostics;

/// <summary>
/// MongoDB index recommendation surfaced for a slow query whose explain plan reported a
/// COLLSCAN (AB#4220 / Stage 2C). The <see cref="ShellCommand"/> is intended for copy-paste
/// execution in mongosh against the tenant database.
/// </summary>
/// <param name="IndexName">
/// Generated index name (<c>field1_1_field2_-1</c> shape). Capped at 127 bytes per
/// MongoDB's hard limit; long compound names get a short SHA-256 suffix to stay distinct.
/// </param>
/// <param name="Fields">
/// Ordered field list per Mongo's ESR rule (Equality → Sort → Range). Useful when the UI
/// wants to render the index spec field-by-field rather than parsing the shell command.
/// </param>
/// <param name="ShellCommand">
/// Ready-to-run mongosh literal, e.g.
/// <c>db.rt_entities.createIndex({"attributes.name.value": 1}, {name: "..."})</c>.
/// </param>
/// <param name="Confidence">
/// <c>"high"</c> / <c>"medium"</c> / <c>"low"</c> — flattened from the engine enum so the API
/// contract stays stable against enum renames.
/// </param>
/// <param name="Notes">
/// Caveats an operator may want to read before running the suggestion (e.g.
/// <c>"$or branches; per-branch indexes may be more selective"</c>). Empty when the
/// suggestion is unambiguous.
/// </param>
/// <param name="CkYamlSnippet">
/// CK-YAML snippet (Stage 2D / AB#4222) the operator can paste into their CK type's source
/// file under the <c>indexes:</c> array. Same index as <see cref="ShellCommand"/> but
/// persisted as part of the model so subsequent re-imports re-create it. <c>null</c> when
/// CK reverse-mapping wasn't possible (filter without <c>ckTypeId.fullName</c>, unknown
/// type, unresolvable field, non-RtEntity collection, or no CK cache wired in the host).
/// </param>
/// <param name="CkTypeFullName">
/// CK type full name the <see cref="CkYamlSnippet"/> belongs to (e.g. <c>Demo/Asset</c>).
/// <c>null</c> when <see cref="CkYamlSnippet"/> is null.
/// </param>
public sealed record SlowQueryIndexSuggestionDto(
    string IndexName,
    IReadOnlyList<SlowQueryIndexFieldDto> Fields,
    string ShellCommand,
    string Confidence,
    IReadOnlyList<string> Notes,
    string? CkYamlSnippet = null,
    string? CkTypeFullName = null);
