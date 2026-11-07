namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;

/// <summary>
///     Result of <c>POST {tenantId}/v1/Models/ValidateCk</c> (AB#8492): a CK model draft validated against the models
///     installed in the tenant without importing it.
/// </summary>
public class CkModelValidationResponseDto
{
    /// <summary>Source kind of a zipped <c>ConstructionKit/</c> folder that the server compiled.</summary>
    public const string SourceKindSource = "Source";

    /// <summary>Source kind of a compiled model (json, yaml or a zip of one).</summary>
    public const string SourceKindCompiled = "Compiled";

    /// <summary>True when the draft has no error. Blockers do not make it invalid (see <see cref="IsBlocked" />).</summary>
    public bool IsValid { get; set; }

    /// <summary>Id of the model (<c>Name-Version</c>) when the draft could be read that far.</summary>
    public string? ModelId { get; set; }

    /// <summary><see cref="SourceKindSource" /> or <see cref="SourceKindCompiled" />; null when the file could not be read.</summary>
    public string? SourceKind { get; set; }

    /// <summary>The errors; empty when valid.</summary>
    public List<CkModelValidationMessageDto> Errors { get; set; } = [];

    /// <summary>Warnings and infos of the compiler; they do not make the draft invalid.</summary>
    public List<CkModelValidationMessageDto> Warnings { get; set; } = [];

    /// <summary>
    ///     True when importing the draft would break an installed model (tenant upgrade guard, AB#6966); the import then
    ///     refuses with 409 <c>CK_UPGRADE_BLOCKED</c> unless forced. Only judged for a draft without errors.
    /// </summary>
    public bool IsBlocked { get; set; }

    /// <summary>The installed models the import would break (empty when not blocked).</summary>
    public List<CkUpgradeBlockerDto> Blockers { get; set; } = [];
}

/// <summary>One finding of the CK model validation (AB#8492).</summary>
public class CkModelValidationMessageDto
{
    /// <summary>Stable message number of the construction kit message catalog (tools branch on it, not on the text).</summary>
    public int MessageNumber { get; set; }

    /// <summary><c>Error</c>, <c>FatalError</c>, <c>Warning</c> or <c>Info</c>.</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>
    ///     File of the draft the finding belongs to, relative to the <c>ConstructionKit/</c> folder (e.g.
    ///     <c>types/pump.yaml</c>), when known. Never a server path.
    /// </summary>
    public string? File { get; set; }

    /// <summary>CK element path the finding belongs to (e.g. <c>MyModel/Pump-1.FlowRate</c>), when known.</summary>
    public string? ElementPath { get; set; }

    /// <summary>Human-readable text; may be reworded.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>The values of the message placeholders by name (machine-readable contract of the message).</summary>
    public Dictionary<string, string> Parameters { get; set; } = [];
}
