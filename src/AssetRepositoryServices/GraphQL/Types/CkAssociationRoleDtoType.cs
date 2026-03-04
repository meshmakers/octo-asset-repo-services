using AssetRepositoryServices.Resources;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using CkAssociationRoleDto = Meshmakers.Octo.Communication.Contracts.DataTransferObjects.CkAssociationRoleDto;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class CkAssociationRoleDtoType : ObjectGraphType<CkAssociationRoleDto>
{
    public CkAssociationRoleDtoType()
    {
        Name = "CkAssociationRole";
        Description = AssetTexts.Graphql_AssociationRole_Description;

        Field(x => x.CkAssociationRoleId, typeof(NonNullGraphType<CkIdGraph<CkAssociationRoleId>>))
            .Description(AssetTexts.Graphql_AssociationRole_CkAssociationRoleId_Description);
        Field(x => x.RtCkAssociationRoleId, typeof(NonNullGraphType<RtCkIdGraph<CkAssociationRoleId>>))
            .Description("Runtime construction kit id of the association role.");
        Field(x => x.InboundName).Description(AssetTexts.Graphql_AssociationRole_InboundName_Description);
        Field(x => x.InboundMultiplicity, typeof(NonNullGraphType<MultiplicitiesDtoType>))
            .Description(AssetTexts.Graphql_AssociationRole_InboundMultiplicity_Description);
        Field(x => x.OutboundName).Description(AssetTexts.Graphql_AssociationRole_OutboundName_Description);
        Field(x => x.OutboundMultiplicity, typeof(NonNullGraphType<MultiplicitiesDtoType>))
            .Description(AssetTexts.Graphql_AssociationRole_OutboundMultiplicity_Description);
        Field(x => x.Description, true).Description(AssetTexts.Graphql_AssociationRole_Description);
    }


    internal static CkAssociationRoleDto CreateCkAssociationRoleDto(CkAssociationRole ckAssociationRole)
    {
        var ckRecordDto = new CkAssociationRoleDto
        {
            CkAssociationRoleId = ckAssociationRole.RoleId,
            RtCkAssociationRoleId = ckAssociationRole.RoleId.ToRtCkId(),
            Description = ckAssociationRole.Description,
            InboundMultiplicity = ckAssociationRole.InboundMultiplicity,
            InboundName = ckAssociationRole.InboundName,
            OutboundMultiplicity = ckAssociationRole.OutboundMultiplicity,
            OutboundName = ckAssociationRole.OutboundName
        };
        return ckRecordDto;
    }
}