using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.Migration;

/// <summary>
///     Straggler timer of the file data migration (AB#6175, Q3): every
///     <see cref="FilesMigrationOptions.StragglerSweepInterval" /> (default 10 min) the tenants whose last
///     sweep found legacy data are swept again. Old reporting pods, adapters with a stale CK cache or old app
///     images can still write System.Reporting file entities after the first move; they are picked up here.
///     Tenants without legacy data are never touched by the timer.
/// </summary>
internal sealed class ReportingFilesSweepBackgroundService : BackgroundService
{
    private readonly ReportingFilesSweepRunner _runner;
    private readonly IOptionsMonitor<FilesMigrationOptions> _options;
    private readonly ILogger<ReportingFilesSweepBackgroundService> _logger;

    public ReportingFilesSweepBackgroundService(ReportingFilesSweepRunner runner,
        IOptionsMonitor<FilesMigrationOptions> options, ILogger<ReportingFilesSweepBackgroundService> logger)
    {
        _runner = runner;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var interval = _options.CurrentValue.StragglerSweepInterval;
                await Task.Delay(interval > TimeSpan.Zero ? interval : TimeSpan.FromMinutes(10), stoppingToken)
                    .ConfigureAwait(false);
                try
                {
                    await _runner.RunStragglerSweepAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{Sweep}: straggler timer run failed; retrying on the next interval",
                        ReportingFilesMigrationConstants.SweepName);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
    }
}
