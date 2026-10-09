namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Files;

/// <summary>
///     What the file API of this cluster offers (AB#6171, <c>GET /{tenant}/v1/files/capabilities</c>).
/// </summary>
public class FilesCapabilitiesDto
{
    /// <summary>Version of the file API contract.</summary>
    public int ApiVersion { get; init; } = 1;

    /// <summary>Largest file one upload may carry, in bytes.</summary>
    public long MaxUploadBytes { get; init; }

    /// <summary>The streamed zip endpoint exists (AB#6225).</summary>
    public bool ZipDownload { get; init; }

    /// <summary>Largest number of files in one zip download.</summary>
    public int ZipMaxFiles { get; init; }

    /// <summary>Largest sum of source sizes of one zip download, in bytes.</summary>
    public long ZipMaxBytes { get; init; }

    /// <summary>Largest file clients should preview inline, in bytes.</summary>
    public long PreviewMaxBytes { get; init; }

    /// <summary>The server deletes folders recursively (GraphQL delete of a folder or root).</summary>
    public bool RecursiveDelete { get; init; } = true;

    /// <summary>The folder statistics endpoint exists.</summary>
    public bool FolderStats { get; init; } = true;

    /// <summary>Well-known names a folder root cannot use.</summary>
    public IReadOnlyCollection<string> ReservedRootNames { get; init; } = [];
}
