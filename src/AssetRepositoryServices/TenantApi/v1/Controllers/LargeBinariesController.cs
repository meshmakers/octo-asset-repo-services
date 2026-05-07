using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;

/// <summary>
///     REST Controller for tenant-specific access to large binaries
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
// ReSharper disable once ClassNeverInstantiated.Global
public class LargeBinariesController : ControllerBase
{
    private const int SniffBufferSize = 16;

    private readonly IOctoService _octoService;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="octoService">Octo service for tenant management</param>
    public LargeBinariesController(IOctoService octoService)
    {
        _octoService = octoService;
    }

    // GET {tenantId}/v1/largeBinaries
    /// <summary>
    ///     Downloads are large binary with given tenantId and large binary id
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    [Authorize(AuthenticationSchemes = InfrastructureCommon.OidcAuthenticationScheme)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get([FromQuery] string largeBinaryId)
    {
        try
        {
            var tenantId = HttpContext.GetTenantId();
            if (string.IsNullOrEmpty(tenantId))
            {
                return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
            }

            if (string.IsNullOrEmpty(largeBinaryId))
            {
                return BadRequest(new InternalServerErrorDto("LargeBinaryId is required"));
            }

            var tenantRepository = await _octoService.SystemContext.FindTenantRepositoryAsync(tenantId);

            using var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
            session.StartTransaction();

            var streamHandler = await tenantRepository.DownloadLargeBinaryAsync(session, OctoObjectId.Parse(largeBinaryId));
            if (streamHandler.Stream == null)
            {
                return NotFound(new ErrorResponse { ErrorMessage = "Large binary not found" });
            }

            await session.CommitTransactionAsync().ConfigureAwait(false);

            // Self-heal old uploads that were stored before content-type detection existed
            // (or under a code path that did not set a specific type). Sniff the magic
            // bytes from the head of the stream so the response carries a useful MIME type
            // — important for callers like <link rel="icon">, where browsers reject
            // application/octet-stream as a favicon.
            var (contentType, responseStream) = await EnsureSpecificContentTypeAsync(
                streamHandler.Stream,
                streamHandler.ContentType);

            return new FileStreamResult(responseStream, contentType);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new InternalServerErrorDto(ex.Message));
        }
        catch (FormatException ex)
        {
            return BadRequest(new InternalServerErrorDto($"Invalid largeBinaryId format: {ex.Message}"));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }

    private static async Task<(string ContentType, Stream Stream)> EnsureSpecificContentTypeAsync(
        Stream stream,
        string? storedContentType)
    {
        if (!BinaryContentTypeDetector.IsGenericOrEmpty(storedContentType))
        {
            return (storedContentType!, stream);
        }

        var buffer = new byte[SniffBufferSize];
        var bytesRead = await ReadUpToAsync(stream, buffer, SniffBufferSize).ConfigureAwait(false);
        var detected = BinaryContentTypeDetector.Detect(buffer.AsSpan(0, bytesRead));

        // Whatever we choose for the response body, the consumer must still see the
        // bytes we already pulled off the source stream. Reset if possible, else
        // prepend the read bytes back onto a wrapper.
        Stream responseStream;
        if (stream.CanSeek)
        {
            stream.Position = 0;
            responseStream = stream;
        }
        else
        {
            var prefix = new byte[bytesRead];
            Array.Copy(buffer, prefix, bytesRead);
            responseStream = new PrependedReadStream(prefix, stream);
        }

        var resolvedContentType =
            BinaryContentTypeDetector.IsGenericOrEmpty(detected)
                ? storedContentType ?? BinaryContentTypeDetector.GenericContentType
                : detected;

        return (resolvedContentType, responseStream);
    }

    private static async Task<int> ReadUpToAsync(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, count - total)).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
