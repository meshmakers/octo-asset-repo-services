using Asp.Versioning;
using Duende.IdentityModel;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.OrphanSweep;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers;

/// <summary>
///     Finds and removes orphaned GridFS bytes of linked binaries per tenant (AB#6541).
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[Route("system/v{version:apiVersion}/files")]
[ApiController]
[ApiVersion("1.0")]
public class FilesOrphanSweepController : ControllerBase
{
    private const double MaxGracePeriodMinutes = 10 * 365 * 24 * 60;

    private readonly IGridFsOrphanSweepService _sweepService;
    private readonly ISystemContext _systemContext;
    private readonly ILogger<FilesOrphanSweepController> _logger;
    private readonly IHostApplicationLifetime _appLifetime;

    /// <summary>
    ///     Constructor
    /// </summary>
    public FilesOrphanSweepController(IGridFsOrphanSweepService sweepService, ISystemContext systemContext,
        ILogger<FilesOrphanSweepController> logger, IHostApplicationLifetime appLifetime)
    {
        _sweepService = sweepService;
        _systemContext = systemContext;
        _logger = logger;
        _appLifetime = appLifetime;
    }

    // POST system/v1/files/orphan-sweep/{tenantId}?remove=false&gracePeriodMinutes=1440
    /// <summary>
    ///     Runs the GridFS orphan sweep of one tenant and returns a count report. A FileSystem GridFS file is an
    ///     orphan when no entity of the tenant references it; referenced bytes are never touched. <b>Dry run is the
    ///     default</b>: the report only counts. Bytes are deleted only with <c>remove=true</c>, and only files older
    ///     than the grace period (default 24 h, minimum 10 minutes). Idempotent and safe to repeat.
    /// </summary>
    /// <remarks>
    ///     The sweep crosses tenant boundaries, so besides the system read-write scope the caller must be signed in to
    ///     the system tenant (<c>tenant_id</c> claim) and, with a user token, hold the role
    ///     <c>AdminPanelManagement</c> (same rule as <c>migration-status</c>). Runbook: dry run on every tenant,
    ///     review the report, then <c>remove=true</c>. The log contains counts only.
    /// </remarks>
    /// <param name="tenantId">The tenant to sweep.</param>
    /// <param name="remove">False (default): report only. True: delete the orphans found.</param>
    /// <param name="gracePeriodMinutes">Only files older than this are considered; default 1440 (24 h), minimum 10.</param>
    /// <param name="cancellationToken">
    ///     Request-aborted token. Honoured by a dry run only; a <c>remove=true</c> run is bound to application
    ///     shutdown instead, so a client or proxy timeout cannot stop it half way through the deletion.
    /// </param>
    [HttpPost("orphan-sweep/{tenantId}")]
    [Authorize(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy)]
    [ProducesResponseType(typeof(GridFsOrphanSweepReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(NotFoundErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(InternalServerErrorDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SweepOrphans(string tenantId, [FromQuery] bool remove = false,
        [FromQuery] double? gracePeriodMinutes = null, CancellationToken cancellationToken = default)
    {
        if (!SystemAdministratorGuard.IsSystemAdministrator(User, _systemContext))
        {
            return Forbid();
        }

        var minutes = gracePeriodMinutes ?? GridFsOrphanSweepOptions.DefaultGracePeriod.TotalMinutes;
        // Written so that NaN is refused too; the upper bound keeps TimeSpan from overflowing.
        if (!(minutes >= GridFsOrphanSweepOptions.MinimumGracePeriod.TotalMinutes && minutes <= MaxGracePeriodMinutes))
        {
            return BadRequest(new OperationFailedErrorDto(
                $"gracePeriodMinutes must be between {GridFsOrphanSweepOptions.MinimumGracePeriod.TotalMinutes} and {MaxGracePeriodMinutes}."));
        }

        var grace = TimeSpan.FromMinutes(minutes);
        // A removal must not be cut off by a client/proxy timeout (partial deletion, no report); only shutdown stops it.
        var sweepToken = remove ? _appLifetime.ApplicationStopping : cancellationToken;

        try
        {
            var report = await _sweepService.SweepAsync(tenantId, remove, grace, sweepToken);
            if (report == null)
            {
                return NotFound(new NotFoundErrorDto($"Tenant '{tenantId}' not found"));
            }

            _logger.LogInformation(
                "GridFS orphan sweep of tenant '{TenantId}' ({Mode}, grace {GraceMinutes} min): examined {Examined}, " +
                "referenced {Referenced}, young {Young}, orphans {Orphans} ({Chunks} chunks, {Bytes} bytes), " +
                "removed {Removed}, {DurationMs} ms",
                tenantId, report.Mode, report.GracePeriodMinutes, report.ExaminedFiles, report.ReferencedFiles,
                report.YoungFiles, report.OrphanFiles, report.OrphanChunks, report.OrphanBytes, report.RemovedFiles,
                report.DurationMilliseconds);
            return Ok(report);
        }
        catch (GridFsOrphanSweepBusyException)
        {
            return Conflict();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "GridFS orphan sweep of tenant '{TenantId}' failed", tenantId);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new InternalServerErrorDto("The orphan sweep failed; see the service log."));
        }
    }
}
