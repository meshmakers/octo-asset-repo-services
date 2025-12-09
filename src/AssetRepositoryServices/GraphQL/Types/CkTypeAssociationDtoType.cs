using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class CkTypeAssociationDtoType : ObjectGraphType<CkTypeAssociationDto>
{
    public CkTypeAssociationDtoType()
    {
        Name = "CkTypeAssociation";
        Description = "Associations of a construction kit type";

        Field(x => x.RoleId, typeof(NonNullGraphType<CkIdGraph<CkAssociationRoleId>>))
            .Description("Construction kit attribute id.");
        Field(x => x.OriginCkTypeId, typeof(NonNullGraphType<CkIdGraph<CkTypeId>>))
            .Description("Type id of the construction kit type of the origin side of the association");
        Field(x => x.TargetCkTypeId, typeof(NonNullGraphType<CkIdGraph<CkTypeId>>))
            .Description("Type id of the construction kit type of the target side of the association");
        Field(x => x.NavigationPropertyName, typeof(NonNullGraphType<StringGraphType>))
            .Description("Navigation property name of the association for the current side");
        Field(x => x.Multiplicity, typeof(NonNullGraphType<MultiplicitiesDtoType>))
            .Description("Multiplicity of the association for the current side");
    }
}