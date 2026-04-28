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
    /// </summary>
    public static FieldFilterOperator MapFieldFilterOperator(FieldFilterOperatorDto op)
    {
        return op switch
        {
            FieldFilterOperatorDto.Equals           => FieldFilterOperator.Equals,
            FieldFilterOperatorDto.NotEquals        => FieldFilterOperator.NotEquals,
            FieldFilterOperatorDto.LessThan         => FieldFilterOperator.LessThan,
            FieldFilterOperatorDto.LessEqualThan    => FieldFilterOperator.LessEqualThan,
            FieldFilterOperatorDto.GreaterThan      => FieldFilterOperator.GreaterThan,
            FieldFilterOperatorDto.GreaterEqualThan => FieldFilterOperator.GreaterEqualThan,
            FieldFilterOperatorDto.Like             => FieldFilterOperator.Like,
            FieldFilterOperatorDto.In               => FieldFilterOperator.In,
            FieldFilterOperatorDto.NotIn            => FieldFilterOperator.NotIn,
            FieldFilterOperatorDto.Between          => FieldFilterOperator.Between,
            FieldFilterOperatorDto.IsNull           => FieldFilterOperator.IsNull,
            FieldFilterOperatorDto.IsNotNull        => FieldFilterOperator.IsNotNull,
            FieldFilterOperatorDto.MatchRegEx       => FieldFilterOperator.MatchRegEx,
            FieldFilterOperatorDto.AnyEq            => FieldFilterOperator.AnyEq,
            FieldFilterOperatorDto.AnyLike          => FieldFilterOperator.AnyLike,
            _ => throw new ArgumentOutOfRangeException(nameof(op), op,
                $"Field filter operator '{op}' is not mapped")
        };
    }

    /// <summary>
    /// Maps a CK model enum (e.g. RtFieldFilterOperatorEnum) to the engine's <see cref="FieldFilterOperator"/>.
    /// Used by persisted queries where the operator is stored as a CK enum.
    /// </summary>
    public static FieldFilterOperator MapCkFieldFilterOperator(Enum op)
    {
        var name = op.ToString();
        return name switch
        {
            "Equals"           => FieldFilterOperator.Equals,
            "NotEquals"        => FieldFilterOperator.NotEquals,
            "LessThan"         => FieldFilterOperator.LessThan,
            "LessEqualThan"    => FieldFilterOperator.LessEqualThan,
            "GreaterThan"      => FieldFilterOperator.GreaterThan,
            "GreaterEqualThan" => FieldFilterOperator.GreaterEqualThan,
            "Like"             => FieldFilterOperator.Like,
            "In"               => FieldFilterOperator.In,
            "NotIn"            => FieldFilterOperator.NotIn,
            "Between"          => FieldFilterOperator.Between,
            "IsNull"           => FieldFilterOperator.IsNull,
            "IsNotNull"        => FieldFilterOperator.IsNotNull,
            _ => throw new ArgumentOutOfRangeException(nameof(op), op,
                $"Field filter operator '{name}' is not mapped")
        };
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
