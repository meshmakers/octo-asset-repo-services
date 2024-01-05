using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public sealed class CkEnumValueDtoType : ObjectGraphType<CkEnumValueDto>
{
    public CkEnumValueDtoType()
    {
        Name = "CkEnumValue";
        Description = "A construction kit enum value";

        Field(x => x.Key, type: typeof(IntGraphType)).Description("Key of the enum");
        Field(x => x.Name, type: typeof(StringGraphType)).Description("Value of the enum");
    }
}