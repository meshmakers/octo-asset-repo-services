using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class CkAttributeMetaDataDtoType: ObjectGraphType<CkAttributeMetaDataDto>
{
    public CkAttributeMetaDataDtoType()
    {
        Name = "CkAttributeMetaData";
        Description = "Construction kit attribute meta data";

        Field(x => x.Key, type: typeof(NonNullGraphType<IdGraphType>))
            .Description("Key of the meta data.");
        Field(x => x.Value, type: typeof(StringGraphType))
            .Description("Value of the meta data.");
        Field(x => x.Description, type: typeof(StringGraphType))
            .Description("Optional description of the meta data.");
    }
}