using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

internal sealed class RtQueryColumnInputDtoType : InputObjectGraphType<RtQueryColumnInputDto>
{
    public RtQueryColumnInputDtoType()
    {
        Name = "RtQueryColumnInput";
        Field(x => x.AggregationType, typeof(NonNullGraphType<AggregationInputTypesDtoType>));
        Field(x => x.AttributePath, typeof(NonNullGraphType<StringGraphType>));
    }
}