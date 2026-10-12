namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;

/// <summary>
///     How the Files routes treat a user token issued for another tenant than the route tenant (AB#6579).
/// </summary>
public enum FilesTenantGuardMode
{
    /// <summary>Refuse with 403 FORBIDDEN (default).</summary>
    Enforce,

    /// <summary>Only log the mismatch and let the request pass (operational fallback, no release needed).</summary>
    LogOnly
}

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

    /// <summary>
    ///     Largest number of entries one delete of a folder or root may remove (default 2,000). GridFS deletes
    ///     do not take part in the mutation transaction, so a cascade must finish well inside the transaction
    ///     lifetime: measured on the local E2E (stage 2), 5,000 entries took 36.4 s of the 60 s limit, too close.
    ///     Stays 2,000 until the index on fs.files.metadata.rtEntityId (story AB#6538) has landed and been
    ///     measured; the cap is raised by measurement after that. Larger trees are deleted subfolder by subfolder.
    /// </summary>
    public int MaxDeleteEntries { get; set; } = 2000;

    /// <summary>
    ///     Interim tenant guard of the Files routes (AB#6579, <c>OCTO_Files__TenantGuard</c>): <c>Enforce</c>
    ///     (default) answers 403 to a user token issued for another tenant, <c>LogOnly</c> only logs it.
    ///     Obsolete once the platform-wide user-token enforcement (AB#5054) is armed.
    /// </summary>
    public FilesTenantGuardMode TenantGuard { get; set; } = FilesTenantGuardMode.Enforce;
}
