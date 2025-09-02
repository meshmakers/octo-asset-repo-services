using AssetRepositoryServices.Resources;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RtQueryCellDtoType : ObjectGraphType<RtQueryCellDto>
{
    public RtQueryCellDtoType()
    {
        Name = "RtQueryCell";
        Description = AssetTexts.Graphql_RtQueryCell_Description;

        Field(x => x.AttributePath, typeof(NonNullGraphType<StringGraphType>))
            .Description(AssetTexts.Graphql_RtQueryCell_AttribuePath_Description);
        Field<SimpleScalarType, object>(nameof(RtQueryCellDto.Value))
            .Description(AssetTexts.Graphql_RtQueryCell_Value_Description);
    }
}