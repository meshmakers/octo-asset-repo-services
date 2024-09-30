using AssetRepositoryServices.Resources;
using GraphQL.Types;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelRepositories;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;

internal class CkExtensionUpdateOperationsDtoType : EnumerationGraphType<CkExtensionUpdateOperations>
{
    public CkExtensionUpdateOperationsDtoType()
    {
        Name = "CkExtensionUpdateOperations";
        Description = AssetTexts.Graphql_Enum_CkExtensionUpdateOperations_Description;
    }
}