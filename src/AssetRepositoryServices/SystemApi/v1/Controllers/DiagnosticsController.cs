using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers;

/// <summary>
/// Manages the diagnostics settings of the service
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[ApiController]
[Route("system/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class DiagnosticsController: ControllerBase
{
    private readonly ILogger<DiagnosticsController> _logger;
    private readonly IDiagnosticsService _diagnosticsService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="diagnosticsService"></param>
    public DiagnosticsController(ILogger<DiagnosticsController> logger, IDiagnosticsService diagnosticsService)
    {
        _logger = logger;
        _diagnosticsService = diagnosticsService;
    }

    /// <summary>
    /// Reconfigures the log level of the service
    /// </summary>
    /// <param name="minLogLevel">The minimal log level to be logged.</param>
    /// <param name="maxLogLevel">The maximal log level to be logged.</param>
    /// <param name="loggerName">The name of the logger to be reconfigured.</param>
    /// <returns></returns>
    [HttpPost("reconfigureLogLevel")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ReconfigureLogLevelAsync([Required] LogLevelDto minLogLevel,
        [Required] LogLevelDto maxLogLevel, string loggerName = "*")
    {
        try
        {
            _logger.LogInformation(
                "Reconfiguring logger {LoggerName} log level to min level {MinLogLevel}, max level {MaxLogLevel}",
                loggerName, minLogLevel, maxLogLevel);
            await _diagnosticsService.ReconfigureLogLevelAsync(minLogLevel, maxLogLevel, loggerName);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new InternalServerErrorDto(ex.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new InternalServerErrorDto(ex.Message));
        }
    }
}