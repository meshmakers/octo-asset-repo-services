using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
/// Consolidated mapper between GraphQL/CK types and the shared runtime query engine contracts.
/// Replaces the scattered operator/aggregation/sort mapping helpers that used to live inside
/// StreamDataQuery.cs. All engine-side targets are shared types from
/// <c>Meshmakers.Octo.Runtime.Contracts.Repositories.Query</c>: <see cref="SortOrderItem"/>,
/// <see cref="FieldFilter"/>, <see cref="AggregationColumn"/>.
/// </summary>
internal static class StreamDataGraphQlMapper
{
    /// <summary>
    /// Maps a SortOrdersDto (GraphQL) to the engine's <see cref="SortOrders"/>.
    /// </summary>
    public static SortOrders MapSortDirection(SortOrdersDto sort)
    {
        return sort switch
        {
            SortOrdersDto.Descending => SortOrders.Descending,
            SortOrdersDto.Ascending => SortOrders.Ascending,
            _ => SortOrders.Default
        };
    }

    /// <summary>
    /// Maps a FieldFilterOperatorDto (GraphQL) to the engine's <see cref="FieldFilterOperator"/>.
    /// AB#4956: the conversion lives next to the DTO so every consumer shares one mapping.
    /// </summary>
    public static FieldFilterOperator MapFieldFilterOperator(FieldFilterOperatorDto op)
    {
        return op.ToFieldFilterOperator();
    }

    /// <summary>
    /// Maps a CK model enum (e.g. RtFieldFilterOperatorEnum) to the engine's <see cref="FieldFilterOperator"/>.
    /// Used by persisted queries where the operator is stored as a CK enum. The mapping is by name, never by
    /// numeric value - the CK enum (System/FieldFilterOperator) and the engine enum are two independent
    /// numberings that only happen to agree up to Match (AB#4956).
    /// </summary>
    public static FieldFilterOperator MapCkFieldFilterOperator(Enum op)
    {
        return FieldFilterOperatorDtoExtensions.FromCkModelEnum(op);
    }

    /// <summary>
    /// Maps a CK model aggregation enum (e.g. RtAggregationTypesEnum) to the engine's
    /// <see cref="AggregationFunction"/>.
    /// </summary>
    public static AggregationFunction MapCkAggregationType(Enum aggregationType)
    {
        var name = aggregationType.ToString();
        return name switch
        {
            "Count"   => AggregationFunction.Count,
            "Sum"     => AggregationFunction.Sum,
            "Average" => AggregationFunction.Average,
            "Avg"     => AggregationFunction.Average,
            "Minimum" => AggregationFunction.Minimum,
            "Min"     => AggregationFunction.Minimum,
            "Maximum" => AggregationFunction.Maximum,
            "Max"     => AggregationFunction.Maximum,
            "TimeWeightedAverage" => AggregationFunction.TimeWeightedAverage,
            "TimeWeightedAvg"     => AggregationFunction.TimeWeightedAverage,
            "StateDuration"       => AggregationFunction.StateDuration,
            _ => throw new ArgumentOutOfRangeException(nameof(aggregationType), aggregationType,
                $"Unknown aggregation type: {name}")
        };
    }

    /// <summary>
    /// Maps the StreamData GraphQL aggregation enum (AggregationFunctionDto) to the engine's
    /// <see cref="AggregationFunction"/>.
    /// </summary>
    public static AggregationFunction MapAggregationFunctionDto(
        Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos.AggregationFunctionDto func)
    {
        return func switch
        {
            Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos.AggregationFunctionDto.Avg
                => AggregationFunction.Average,
            Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos.AggregationFunctionDto.Min
                => AggregationFunction.Minimum,
            Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos.AggregationFunctionDto.Max
                => AggregationFunction.Maximum,
            Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos.AggregationFunctionDto.Count
                => AggregationFunction.Count,
            Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos.AggregationFunctionDto.Sum
                => AggregationFunction.Sum,
            Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos.AggregationFunctionDto.TimeWeightedAvg
                => AggregationFunction.TimeWeightedAverage,
            Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos.AggregationFunctionDto.StateDuration
                => AggregationFunction.StateDuration,
            _ => throw new ArgumentOutOfRangeException(nameof(func), func, null)
        };
    }

    /// <summary>
    /// Maps a list of GraphQL SortDto to engine <see cref="SortOrderItem"/>.
    /// </summary>
    public static IReadOnlyList<SortOrderItem>? MapSortOrders(IEnumerable<SortDto>? sortDtos)
    {
        if (sortDtos == null) return null;
        var list = sortDtos
            .Select(s => new SortOrderItem(s.AttributePath, MapSortDirection(s.SortOrder)))
            .ToList();
        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// Maps a list of GraphQL FieldFilterDto to engine <see cref="FieldFilter"/>.
    /// Filters with null ComparisonValue are kept only for IsNull/IsNotNull (which don't need a value).
    /// </summary>
    public static IReadOnlyList<FieldFilter>? MapFieldFilters(IEnumerable<FieldFilterDto>? filters)
    {
        if (filters == null) return null;
        var list = filters
            .Where(f => IsNullCheck(f.Operator) || f.ComparisonValue != null)
            .Select(f => new FieldFilter(
                f.AttributePath,
                MapFieldFilterOperator(f.Operator),
                f.ComparisonValue,
                f.SecondaryValue))
            .ToList();
        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// Maps a list of CK-model field filter entities (from a persisted query)
    /// to engine <see cref="FieldFilter"/>. Filters with null ComparisonValue are kept only for
    /// IsNull/IsNotNull.
    ///
    /// Expects items with AttributePath (string), Operator (enum), and ComparisonValue (object?).
    /// </summary>
    public static IReadOnlyList<FieldFilter>? MapCkFieldFilters<T>(IEnumerable<T>? filters,
        Func<T, string> pathSelector,
        Func<T, Enum> operatorSelector,
        Func<T, object?> valueSelector)
    {
        if (filters == null) return null;
        var list = filters
            .Where(f =>
            {
                var op = MapCkFieldFilterOperator(operatorSelector(f));
                return IsNullCheck(op) || valueSelector(f) != null;
            })
            .Select(f => new FieldFilter(
                pathSelector(f),
                MapCkFieldFilterOperator(operatorSelector(f)),
                valueSelector(f)))
            .ToList();
        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// Maps CK-model sort items (from a persisted query) to engine <see cref="SortOrderItem"/>.
    /// Uses string-based enum mapping via name.
    /// </summary>
    public static IReadOnlyList<SortOrderItem>? MapCkSortOrders<T>(
        IEnumerable<T>? sortItems,
        Func<T, string> pathSelector,
        Func<T, Enum> sortOrderSelector)
    {
        if (sortItems == null) return null;
        var list = sortItems
            .Select(s => new SortOrderItem(
                pathSelector(s),
                sortOrderSelector(s).ToString() == "Descending"
                    ? SortOrders.Descending
                    : SortOrders.Ascending))
            .ToList();
        return list.Count > 0 ? list : null;
    }

    private static bool IsNullCheck(FieldFilterOperatorDto op) =>
        op is FieldFilterOperatorDto.IsNull or FieldFilterOperatorDto.IsNotNull;

    private static bool IsNullCheck(FieldFilterOperator op) =>
        op is FieldFilterOperator.IsNull or FieldFilterOperator.IsNotNull;
}
