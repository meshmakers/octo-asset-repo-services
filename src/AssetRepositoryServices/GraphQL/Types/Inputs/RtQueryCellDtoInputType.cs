using AssetRepositoryServices.Resources;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class RtQueryCellDtoInputType : InputObjectGraphType<RtQueryCellDto>
{
    public RtQueryCellDtoInputType()
    {
        Name = $"RtQueryCell{Statics.GraphQlInputSuffix}";
        Description = AssetTexts.Graphql_RtQueryCellInput_Description;

        Field(x => x.AttributePath, typeof(StringGraphType))
            .Description(AssetTexts.Graphql_RtQueryCell_AttribuePath_Description);
        Field<SimpleScalarType, object>(nameof(RtEntityAttributeDto.Value))
            .Description(AssetTexts.Graphql_RtQueryCell_Value_Description);
    }
}