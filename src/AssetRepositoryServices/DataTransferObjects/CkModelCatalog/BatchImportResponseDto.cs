namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;

/// <summary>
///     Response for batch import containing a job ID per imported model.
///     The frontend must wait for each job sequentially to ensure dependency order.
/// </summary>
public class BatchImportResponseDto
{
    /// <summary>
    ///     Job IDs in dependency order (one per model)
    /// </summary>
    public List<string> JobIds { get; set; } = [];
}
