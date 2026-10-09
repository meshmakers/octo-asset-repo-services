namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Files;

/// <summary>
///     Request of a streamed zip download (AB#6225): files and folders addressed by path and/or rtId.
///     Folders are included with everything below them.
/// </summary>
public class ZipRequestDto
{
    /// <summary>Entries addressed by root well-known name + path.</summary>
    public List<FileRefDto>? Items { get; set; }

    /// <summary>Entries addressed by rtId.</summary>
    public List<string>? RtIds { get; set; }

    /// <summary>File name of the zip (default "files.zip").</summary>
    public string? FileName { get; set; }
}

/// <summary>
///     A file system entry addressed by path.
/// </summary>
public class FileRefDto
{
    /// <summary>Well-known name of the folder root.</summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>Path below the root; empty for the root itself.</summary>
    public string? Path { get; set; }
}
