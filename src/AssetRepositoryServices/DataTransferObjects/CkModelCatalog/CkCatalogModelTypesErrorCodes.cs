namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;

/// <summary>
///     Stable codes of GET system/v1/ckmodelcatalog/{catalogName}/{modelId}/types, carried in
///     the <c>code</c> extension of the application/problem+json answer, so callers branch on the code and never on the message.
/// </summary>
public static class CkCatalogModelTypesErrorCodes
{
    /// <summary>The catalog name is not one of the configured CK model catalogs (404).</summary>
    public const string CkCatalogNotFound = "CkCatalogNotFound";

    /// <summary>The catalog has no such model version (404).</summary>
    public const string CkCatalogModelNotFound = "CkCatalogModelNotFound";

    /// <summary>The model id is not a CK model id with version, e.g. "Basic-2.7.0" (400).</summary>
    public const string InvalidCkModelId = "InvalidCkModelId";
}
