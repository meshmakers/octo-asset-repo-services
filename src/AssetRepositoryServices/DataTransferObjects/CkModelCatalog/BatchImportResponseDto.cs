namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;

/// <summary>
///     Response for batch import containing a single job ID for the sequential batch import.
///     All models are imported sequentially within a single Hangfire job to prevent
///     dependency resolution race conditions.
/// </summary>
public class BatchImportResponseDto
{
    /// <summary>
    ///     Single job ID for the batch import job that imports all models sequentially
    /// </summary>
    public string JobId { get; set; } = string.Empty;
}
