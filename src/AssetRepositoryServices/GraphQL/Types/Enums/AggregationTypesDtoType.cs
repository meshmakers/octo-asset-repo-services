using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;

internal class AggregationTypesDtoType : EnumerationGraphType<AggregationTypesDto>
{
    public AggregationTypesDtoType()
    {
        Name = "AggregationTypes";
        Description = "Defines the type of aggregation for runtime query results.";
    }
}
