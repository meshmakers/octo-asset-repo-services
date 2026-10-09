namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;

/// <summary>
///     Per-cluster limits of the platform file system (AB#6171 S2, configuration section <c>Files</c>,
///     environment variables <c>OCTO_Files__…</c>). The defaults are the accepted platform limits.
/// </summary>
public class FilesOptions
{
    /// <summary>
    ///     Name of the configuration section.
    /// </summary>
    public const string SectionName = "Files";

    /// <summary>
    ///     Largest file a single upload may carry, in bytes (default 100 MB). The ingress in front of the
    ///     service must accept a little more (octo-helm-core: 110m).
    /// </summary>
    public long MaxUploadBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>
    ///     Largest number of files one zip download may contain (default 1,000).
    /// </summary>
    public int ZipMaxFiles { get; set; } = 1000;

    /// <summary>
    ///     Largest sum of the source file sizes of one zip download, in bytes (default 500 MB).
    /// </summary>
    public long ZipMaxBytes { get; set; } = 500L * 1024 * 1024;

    /// <summary>
    ///     Largest file clients should preview inline, in bytes (default 20 MB). Advertised through
    ///     <c>GET /{tenant}/v1/files/capabilities</c>; the server does not enforce it.
    /// </summary>
    public long PreviewMaxBytes { get; set; } = 20L * 1024 * 1024;
}
