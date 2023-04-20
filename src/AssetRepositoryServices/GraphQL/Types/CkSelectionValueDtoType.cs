using GraphQL.Types;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public class CkSelectionValueDtoType : ObjectGraphType<CkSelectionValueDto>
{
    public CkSelectionValueDtoType()
    {
        Name = "CkSelectionValue";
        Description = "a person or company";

        Field(x => x.Key, type: typeof(IntGraphType)).Description("AssociationId of the selection list value");
        Field(x => x.Name, type: typeof(StringGraphType)).Description("Name of the selection list value");
    }
}
