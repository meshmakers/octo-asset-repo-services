using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public sealed class CkEnumDtoType : ObjectGraphType<CkEnumDto>
{
    public CkEnumDtoType()
    {
        Name = "CkEnum";
        Description = "A construction kit enum";

        Field(x => x.CkEnumId, type: typeof(NonNullGraphType<CkIdType<CkEnumId>>)).Description("Unique id of the enum.");
        Field(x => x.UseFlags, type: typeof(NonNullGraphType<BooleanGraphType>)).Description("Use flags for the enum.");
        Field(x => x.Values, type: typeof(NonNullGraphType<ListGraphType<CkEnumValueDtoType>>)).Description("Value of the enum");
    }
}