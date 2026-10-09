namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Files;

/// <summary>
///     Request of <c>POST {tenant}/v1/files/linked-counts</c>: files (or folders) whose links to other entities
///     should be counted (AB#6171).
/// </summary>
public class LinkedCountsRequestDto
{
    /// <summary>Runtime ids (at most 500 per request).</summary>
    public List<string> RtIds { get; set; } = [];
}

/// <summary>
///     Number of associations of each entry other than the folder structure (<c>System/ParentChild</c>), any role,
///     any direction. Entries the caller cannot see (or that do not exist) are left out.
/// </summary>
public class LinkedCountsDto
{
    /// <summary>rtId → number of links.</summary>
    public Dictionary<string, int> Counts { get; init; } = new();
}
