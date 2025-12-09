using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class CkTypeAssociationDirectionDtoType : ObjectGraphType<CkTypeAssociationDirectionDto>
{
    public CkTypeAssociationDirectionDtoType()
    {
        Name = "CkTypeAssociationDirection";
        Description = "Returns inbound and outbound association definitions";

        Field<CkTypeAssociationSourceDtoType>("in")
            .Description("Gets ingoing associations")
            .Returns<CkTypeAssociationSourceDto>()
            .Resolve(ctx => new CkTypeAssociationSourceDto
                { CkTypeId = ctx.Source.CkTypeId, Direction = GraphDirections.Inbound });
        Field<CkTypeAssociationSourceDtoType>("out")
            .Description("Gets outgoing associations")
            .Returns<CkTypeAssociationSourceDto>()
            .Resolve(ctx => new CkTypeAssociationSourceDto
                { CkTypeId = ctx.Source.CkTypeId, Direction = GraphDirections.Outbound });
    }
}