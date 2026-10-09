namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.Migration;

/// <summary>
///     Registration of the System.Reporting → System.Files file data migration (AB#6175).
/// </summary>
public static class FilesMigrationServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the sweep, the pre-check, the straggler tracker and the straggler timer. Options are
    ///     bound in <c>Program.cs</c> (section <see cref="FilesMigrationOptions.SectionName" />); without a
    ///     binding the defaults apply.
    /// </summary>
    public static IServiceCollection AddReportingFilesMigration(this IServiceCollection services)
    {
        services.AddOptions<FilesMigrationOptions>();
        services.AddSingleton<ITenantMongoDatabaseProvider, TenantMongoDatabaseProvider>();
        services.AddSingleton<ReportingFilesSweepTracker>();
        services.AddSingleton<ReportingFilesMoveSweep>();
        services.AddSingleton<FilesMigrationStatusService>();
        services.AddSingleton<ReportingFilesSweepRunner>();
        services.AddHostedService<ReportingFilesSweepBackgroundService>();
        return services;
    }
}
