namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Files;

/// <summary>
///     Deep statistics of a folder or root (AB#6171): what the caller can see below it, plus how many
///     entries are hidden from the caller by data permissions.
/// </summary>
public class FolderStatsDto
{
    /// <summary>Visible folders below the folder (all levels).</summary>
    public long Folders { get; init; }

    /// <summary>Visible files below the folder (all levels).</summary>
    public long Files { get; init; }

    /// <summary>Total size of the visible files, in bytes.</summary>
    public long Bytes { get; init; }

    /// <summary>Visible files linked to another entity (any association other than the folder tree).</summary>
    public long LinkedFiles { get; init; }

    /// <summary>
    ///     Entries below the folder the caller cannot see (data permissions). A delete of the folder includes
    ///     them and fails when the caller may not delete them.
    /// </summary>
    public long HiddenEntries { get; init; }

    /// <summary>False when the walk hit its limit (counts are lower bounds).</summary>
    public bool Complete { get; init; }

    /// <summary>Up to five linked files with up to three of their visible linked entities.</summary>
    public IReadOnlyList<LinkedFileSampleDto> LinkedSamples { get; init; } = [];
}

/// <summary>
///     A file linked to business entities.
/// </summary>
public class LinkedFileSampleDto
{
    /// <summary>Runtime id of the file.</summary>
    public required string RtId { get; init; }

    /// <summary>Name of the file.</summary>
    public required string Name { get; init; }

    /// <summary>Path below the folder the statistics were asked for.</summary>
    public required string Path { get; init; }

    /// <summary>Linked entities visible to the caller.</summary>
    public IReadOnlyList<LinkedEntityDto> Entities { get; init; } = [];
}

/// <summary>
///     An entity a file is linked to.
/// </summary>
public class LinkedEntityDto
{
    /// <summary>Runtime id.</summary>
    public required string RtId { get; init; }

    /// <summary>Type id.</summary>
    public required string CkTypeId { get; init; }

    /// <summary>Display name (display name, name attribute, well-known name or rtId).</summary>
    public required string DisplayName { get; init; }
}
