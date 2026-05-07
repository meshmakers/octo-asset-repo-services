using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Xunit;

namespace AssetRepositoryServices.UnitTests.Services;

public class BinaryContentTypeDetectorTests
{
    [Fact]
    public void Detect_ReturnsImagePng_ForPngMagicBytes()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        BinaryContentTypeDetector.Detect(bytes).Should().Be("image/png");
    }

    [Fact]
    public void Detect_ReturnsImageJpeg_ForJpegMagicBytes()
    {
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
        BinaryContentTypeDetector.Detect(bytes).Should().Be("image/jpeg");
    }

    [Fact]
    public void Detect_ReturnsImageGif_ForGifMagicBytes()
    {
        var bytes = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 };
        BinaryContentTypeDetector.Detect(bytes).Should().Be("image/gif");
    }

    [Fact]
    public void Detect_ReturnsImageWebp_ForWebpMagicBytes()
    {
        var bytes = new byte[]
        {
            0x52, 0x49, 0x46, 0x46, // RIFF
            0x00, 0x00, 0x00, 0x00, // size placeholder
            0x57, 0x45, 0x42, 0x50  // WEBP
        };
        BinaryContentTypeDetector.Detect(bytes).Should().Be("image/webp");
    }

    [Fact]
    public void Detect_ReturnsImageBmp_ForBmpMagicBytes()
    {
        var bytes = new byte[] { 0x42, 0x4D, 0x00, 0x00 };
        BinaryContentTypeDetector.Detect(bytes).Should().Be("image/bmp");
    }

    [Fact]
    public void Detect_ReturnsImageIcon_ForIcoMagicBytes()
    {
        var bytes = new byte[] { 0x00, 0x00, 0x01, 0x00 };
        BinaryContentTypeDetector.Detect(bytes).Should().Be("image/x-icon");
    }

    [Fact]
    public void Detect_ReturnsApplicationPdf_ForPdfMagicBytes()
    {
        var bytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31 };
        BinaryContentTypeDetector.Detect(bytes).Should().Be("application/pdf");
    }

    [Fact]
    public void Detect_ReturnsImageSvg_ForSvgMarker()
    {
        var bytes = "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"u8.ToArray();
        BinaryContentTypeDetector.Detect(bytes).Should().Be("image/svg+xml");
    }

    [Fact]
    public void Detect_ReturnsImageSvg_ForXmlPrologueWithSvg()
    {
        var bytes = "<?xml version=\"1.0\"?><svg></svg>"u8.ToArray();
        BinaryContentTypeDetector.Detect(bytes).Should().Be("image/svg+xml");
    }

    [Fact]
    public void Detect_ReturnsGenericContentType_ForUnknownBytes()
    {
        var bytes = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        BinaryContentTypeDetector.Detect(bytes).Should().Be("application/octet-stream");
    }

    [Fact]
    public void Detect_ReturnsGenericContentType_ForEmptyData()
    {
        BinaryContentTypeDetector.Detect(ReadOnlySpan<byte>.Empty).Should().Be("application/octet-stream");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("application/octet-stream")]
    [InlineData("APPLICATION/OCTET-STREAM")]
    public void IsGenericOrEmpty_ReturnsTrue_ForGenericInputs(string? input)
    {
        BinaryContentTypeDetector.IsGenericOrEmpty(input).Should().BeTrue();
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/svg+xml")]
    [InlineData("application/pdf")]
    public void IsGenericOrEmpty_ReturnsFalse_ForSpecificContentTypes(string input)
    {
        BinaryContentTypeDetector.IsGenericOrEmpty(input).Should().BeFalse();
    }

    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/gif", ".gif")]
    [InlineData("image/webp", ".webp")]
    [InlineData("image/svg+xml", ".svg")]
    [InlineData("image/bmp", ".bmp")]
    [InlineData("image/x-icon", ".ico")]
    [InlineData("application/pdf", ".pdf")]
    [InlineData("application/octet-stream", ".bin")]
    [InlineData(null, ".bin")]
    public void DetectFileExtension_ReturnsExpected(string? contentType, string expected)
    {
        BinaryContentTypeDetector.DetectFileExtension(contentType).Should().Be(expected);
    }
}
