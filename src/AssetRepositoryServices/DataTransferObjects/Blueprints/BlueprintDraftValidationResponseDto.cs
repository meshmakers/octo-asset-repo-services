namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

/// <summary>
///     Result of <c>POST {tenantId}/v1/blueprints/validate</c>: a blueprint draft validated with the shared
///     blueprint draft validator (the same checks as <c>octo-bpm validate</c> plus the CK model dependency ranges against
///     the CK model catalogs). Nothing is written.
/// </summary>
public class BlueprintDraftValidationResponseDto
{
    /// <summary>At most this many findings are returned; <see cref="TotalFindings" /> counts all.</summary>
    public const int MaxFindings = 200;

    /// <summary>True when no finding is an error.</summary>
    public bool IsValid { get; set; }

    /// <summary>Id of the blueprint (<c>Name-Version</c>) when the manifest could be read.</summary>
    public string? BlueprintId { get; set; }

    /// <summary>Number of all findings (the list is bounded to <see cref="MaxFindings" />).</summary>
    public int TotalFindings { get; set; }

    /// <summary>True when <see cref="Findings" /> was cut to <see cref="MaxFindings" />.</summary>
    public bool Truncated { get; set; }

    /// <summary>The findings, errors first.</summary>
    public List<BlueprintDraftFindingDto> Findings { get; set; } = [];
}

/// <summary>One finding of the blueprint draft validation.</summary>
public class BlueprintDraftFindingDto
{
    /// <summary>Stable message number of the construction kit message catalog (149-167); tools branch on it.</summary>
    public int Code { get; set; }

    /// <summary><c>Error</c>, <c>FatalError</c>, <c>Warning</c> or <c>Info</c>.</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>File of the draft, relative to the blueprint root (e.g. <c>seed-data/entities.yaml</c>). Never a server path.</summary>
    public string? File { get; set; }

    /// <summary>CK model the finding is about (dependency findings), when known.</summary>
    public string? ModelId { get; set; }

    /// <summary>Human-readable text; may be reworded.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>The values of the message placeholders by name (machine-readable contract of the message).</summary>
    public Dictionary<string, string> Parameters { get; set; } = [];
}
