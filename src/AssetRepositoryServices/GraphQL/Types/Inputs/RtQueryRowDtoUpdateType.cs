using AssetRepositoryServices.Resources;
using GraphQL.Types;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class RtQueryRowDtoUpdateType : InputObjectGraphType<MutationDto<RtSimpleQueryRowDto>>
{
    public RtQueryRowDtoUpdateType()
    {
        Name = $"RtQueryRow{Statics.GraphQlUpdatePrefix}".ToPascalCase();
        Field(x => x.RtId, typeof(OctoObjectIdType));
        Field<RtQueryRowDtoInputType>("item").Description(AssetTexts.Graphql_RtQueryRowUpdate_Item_Description);
    }
}