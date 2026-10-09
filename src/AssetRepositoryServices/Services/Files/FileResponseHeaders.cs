using System.Text;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;

/// <summary>
///     Response headers of file downloads (AB#6171 S2c): RFC 6266 <c>Content-Disposition</c> with an ASCII
///     fallback and <c>filename*=UTF-8''…</c>, the headers a browser client must be allowed to read
///     (<c>Access-Control-Expose-Headers</c>; the shared CORS policy does not expose them yet), and safe
///     delivery of active content: SVG, HTML, XML and script types never run in the service origin —
///     they get a sandboxing Content-Security-Policy and are never served inline unless asked for.
/// </summary>
public static class FileResponseHeaders
{
    /// <summary>
    ///     Value of <c>Access-Control-Expose-Headers</c> on file responses.
    /// </summary>
    public const string ExposedHeaders = "Content-Disposition, Content-Length, Content-Type, ETag";

    /// <summary>
    ///     CSP of file responses: no script, no plugins, no network, styles and data images only (enough
    ///     to display an SVG or a text file without running anything).
    /// </summary>
    public const string SandboxPolicy = "sandbox; default-src 'none'; img-src data:; style-src 'unsafe-inline'";

    private static readonly string[] ActiveContentTypes =
    [
        "image/svg+xml", "text/html", "application/xhtml+xml", "text/xml", "application/xml",
        "text/javascript", "application/javascript", "application/ecmascript", "text/ecmascript"
    ];

    /// <summary>
    ///     True for content types a browser can execute (script inside SVG/HTML/XML, JavaScript).
    /// </summary>
    public static bool IsActiveContent(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var mediaType = contentType.Split(';')[0].Trim();
        return ActiveContentTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase) ||
               mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Builds the <c>Content-Disposition</c> value: <c>attachment; filename="ascii"; filename*=UTF-8''pct</c>.
    /// </summary>
    public static string ContentDisposition(string? fileName, bool inline)
    {
        var type = inline ? "inline" : "attachment";
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return type;
        }

        return $"{type}; filename=\"{AsciiFallback(fileName)}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }

    /// <summary>
    ///     Applies the download headers. <paramref name="inline" /> is honoured for passive content only;
    ///     active content is always an attachment with a sandboxing CSP.
    /// </summary>
    public static void Apply(HttpResponse response, string? fileName, string? contentType, bool inline,
        string? etag = null)
    {
        var active = IsActiveContent(contentType);
        response.Headers.ContentDisposition = ContentDisposition(fileName, inline && !active);
        response.Headers.AccessControlExposeHeaders = ExposedHeaders;
        response.Headers.XContentTypeOptions = "nosniff";
        if (active || !inline)
        {
            // Inline passive content (PDF, images, text) keeps working in the browser's own viewers; anything
            // that could execute, and every attachment, is sandboxed should it be opened in the browser.
            response.Headers.ContentSecurityPolicy = SandboxPolicy;
        }

        response.Headers.CacheControl = "private, no-cache";
        if (!string.IsNullOrEmpty(etag))
        {
            response.Headers.ETag = $"\"{etag}\"";
        }
    }

    private static string AsciiFallback(string fileName)
    {
        var builder = new StringBuilder(fileName.Length);
        foreach (var c in fileName.Normalize(NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) ==
                System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue; // "ä" -> "a"
            }

            builder.Append(c is >= ' ' and <= '~' && c != '"' && c != '\\' ? c : '_');
        }

        return builder.ToString();
    }
}
