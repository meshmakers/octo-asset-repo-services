using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class CkAttributeMetaDataDtoType : ObjectGraphType<CkAttributeMetaDataDto>
{
    public CkAttributeMetaDataDtoType()
    {
        Name = "CkAttributeMetaData";
        Description = "Construction kit attribute meta data";

        Field(x => x.Key, typeof(NonNullGraphType<IdGraphType>))
            .Description("Key of the meta data.");
        Field(x => x.Value, typeof(StringGraphType))
            .Description("Value of the meta data.");
        Field(x => x.Description, typeof(StringGraphType))
            .Description("Optional description of the meta data.");
    }

    public static CkAttributeMetaDataDto CreateCkAttributeMetaDataDto(
        ConstructionKit.Contracts.DataTransferObjects.CkAttributeMetaDataDto meta)
    {
        return new CkAttributeMetaDataDto
        {
            Key = meta.Key,
            Value = meta.Value,
            Description = meta.Description
        };
    }
}