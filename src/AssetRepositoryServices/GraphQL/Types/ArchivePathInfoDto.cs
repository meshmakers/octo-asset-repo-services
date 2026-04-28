using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// One row of the <c>availableArchivePaths</c> GraphQL query (concept §16). Describes a single
/// attribute path reachable from a CK type that the studio can offer when the user composes a
/// <c>CkArchive</c>.
/// </summary>
internal sealed record ArchivePathInfoDto(
    string Path,
    AttributeValueTypesDto? PrimitiveType,
    bool IsRecord,
    bool IsArray,
    CkId<CkRecordId>? RecordTypeId,
    CkId<CkTypeId>? InheritedFromCkTypeId);
