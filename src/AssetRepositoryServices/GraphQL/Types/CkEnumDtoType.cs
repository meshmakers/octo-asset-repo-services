using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class CkEnumDtoType : ObjectGraphType<CkEnumDto>
{
    public CkEnumDtoType()
    {
        Name = "CkEnum";
        Description = "A construction kit enum";

        Field(x => x.CkEnumId, type: typeof(NonNullGraphType<CkIdTypeGraph<CkEnumId>>)).Description("Unique id of the enum.");
        Field(x => x.UseFlags, type: typeof(NonNullGraphType<BooleanGraphType>)).Description("Use flags for the enum.");
        Field(x => x.Values, type: typeof(NonNullGraphType<ListGraphType<CkEnumValueDtoType>>)).Description("Value of the enum");
    }
    
    internal static CkEnumDto CreateCkEnumDto(CkEnumGraph ckEnumGraph)
    {
        var ckEnumDto = new CkEnumDto
        {
            CkEnumId = ckEnumGraph.CkEnumId,
            UseFlags = ckEnumGraph.UseFlags,
            Values = ckEnumGraph.Values.Select(CkEnumValueDtoType.CreateCkEnumValueDto).ToList()
        };
        return ckEnumDto;
    }
    
    internal static CkEnumDto CreateCkEnumDto(CkEnum ckEnumGraph)
    {
        var ckEnumDto = new CkEnumDto
        {
            CkEnumId = ckEnumGraph.CkEnumId,
            UseFlags = ckEnumGraph.UseFlags,
            Values = ckEnumGraph.Values.Select(CkEnumValueDtoType.CreateCkEnumValueDto).ToList()
        };
        return ckEnumDto;
    }
}