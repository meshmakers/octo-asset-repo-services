using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;

internal class AggregationInputTypesDtoType : EnumerationGraphType<AggregationInputTypesDto>
{
    public AggregationInputTypesDtoType()
    {
        Name = "AggregationInputTypes";
        Description = "Defines the type of aggregation for runtime queries.";
    }
}