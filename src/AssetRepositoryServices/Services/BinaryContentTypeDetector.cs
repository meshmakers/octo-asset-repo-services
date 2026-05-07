using System.Text;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services;

/// <summary>
/// Detects the MIME content type of binary data by inspecting magic bytes.
/// Used both at upload time (to record the correct ContentType in storage) and at
/// download time (to recover from entries that were uploaded before detection
/// existed and ended up stored as application/octet-stream).
/// </summary>
public static class BinaryContentTypeDetector
{
    /// <summary>
    /// Generic fallback content type used when the binary format cannot be inferred.
    /// </summary>
    public const string GenericContentType = "application/octet-stream";

    /// <summary>
    /// Returns true when <paramref name="contentType"/> is null, empty, or the generic
    /// fallback type that carries no useful information for clients.
    /// </summary>
    public static bool IsGenericOrEmpty(string? contentType)
    {
        return string.IsNullOrWhiteSpace(contentType)
               || string.Equals(contentType, GenericContentType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Inspects the leading bytes of <paramref name="data"/> and returns the matching
    /// MIME content type, or <see cref="GenericContentType"/> when nothing matches.
    /// </summary>
    public static string Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4)
        {
            // PNG: 89 50 4E 47
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
                return "image/png";

            // GIF: 47 49 46 38
            if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38)
                return "image/gif";

            // PDF: 25 50 44 46
            if (data[0] == 0x25 && data[1] == 0x50 && data[2] == 0x44 && data[3] == 0x46)
                return "application/pdf";

            // ICO: 00 00 01 00
            if (data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x01 && data[3] == 0x00)
                return "image/x-icon";

            // WebP: 52 49 46 46 xx xx xx xx 57 45 42 50
            if (data.Length >= 12 &&
                data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
                data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
                return "image/webp";
        }

        if (data.Length >= 3)
        {
            // JPEG: FF D8 FF
            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
                return "image/jpeg";
        }

        if (data.Length >= 2)
        {
            // BMP: 42 4D
            if (data[0] == 0x42 && data[1] == 0x4D)
                return "image/bmp";
        }

        // SVG: text-based, scan a small prefix for SVG/XML markers
        if (data.Length >= 5 && data[0] == 0x3C)
        {
            var sniffLength = Math.Min(data.Length, 256);
            var header = Encoding.UTF8.GetString(data[..sniffLength]).TrimStart();
            if (header.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
                || header.StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
                return "image/svg+xml";
        }

        return GenericContentType;
    }

    /// <summary>
    /// Returns the conventional file extension (including leading dot) for a known
    /// content type, or <c>.bin</c> when the content type is unknown.
    /// </summary>
    public static string DetectFileExtension(string? contentType)
    {
        return contentType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            "image/bmp" => ".bmp",
            "image/x-icon" => ".ico",
            "application/pdf" => ".pdf",
            _ => ".bin"
        };
    }
}
