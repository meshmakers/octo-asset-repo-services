namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.Migration;

/// <summary>
///     Options of the move of System.Reporting file data to System.Files (AB#6175, configuration section
///     <c>FilesMigration</c>, environment variables <c>OCTO_FilesMigration__…</c>).
/// </summary>
public class FilesMigrationOptions
{
    /// <summary>
    ///     Name of the configuration section.
    /// </summary>
    public const string SectionName = "FilesMigration";

    /// <summary>
    ///     Kill switch of the sweep (tenant start and straggler timer). The pre-check endpoint keeps working.
    /// </summary>
    public bool SweepEnabled { get; set; } = true;

    /// <summary>
    ///     Interval of the straggler sweep for tenants whose last sweep found legacy data (default 10 min, Q3).
    /// </summary>
    public TimeSpan StragglerSweepInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     Entities moved per transaction.
    /// </summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    ///     Largest number of literal references the pre-check lists (the count is always complete).
    /// </summary>
    public int MaxReportedReferences { get; set; } = 1000;

    /// <summary>
    ///     Largest number of orphan ids the pre-check lists (the count is always complete).
    /// </summary>
    public int MaxReportedOrphans { get; set; } = 100;
}
