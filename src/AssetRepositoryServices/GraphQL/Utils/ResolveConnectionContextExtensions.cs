using System.Diagnostics.CodeAnalysis;
using GraphQL;
using GraphQL.Builders;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Geospatial.Geometry;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using StringExtensions = GraphQL.StringExtensions;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

internal static class ResolveConnectionContextExtensions
{
    internal static int? GetOffset<TEntity>(this IResolveConnectionContext<TEntity> ctx)
    {
        int? offset = null;
        if (!string.IsNullOrEmpty(ctx.After))
        {
            offset = ConnectionUtils.CursorToOffset(ctx.After) + 1;
        }

        return offset;
    }

    internal static bool TryGetArgument<TType>(this IResolveFieldContext context, string name, [NotNullWhen(true)] out TType? value)
    {
        return TryGetArgument(context, name, default, out value);
    }

    internal static bool TryGetArgument<TType>(this IResolveFieldContext context, string name, TType defaultValue,
        [NotNullWhen(true)] out TType value)
    {
        if (context.HasArgument(name))
        {
            value = context.GetArgument<TType>(name);
            if (value != null)
            {
                return true;
            }
        }

        value = defaultValue;
        return false;
    }

    internal static DataQueryOperation GetDataQueryOperation<TEntity>(this IResolveConnectionContext<TEntity> ctx, DataQueryOperation? operation = null)
    {
        var dataQueryOperation = operation ?? DataQueryOperation.Create();

        if (ctx.TryGetArgument(Statics.SearchFilterArg, out SearchFilterDto? filterDto))
        {
            dataQueryOperation = dataQueryOperation.UseLanguage(filterDto.Language ?? "en");
            if (filterDto.Type == null || filterDto.Type == SearchFilterTypesDto.TextSearch)
            {
                ArgumentValidation.ValidateString(nameof(filterDto.Language), filterDto.Language);

                if (filterDto.SearchTerm != null)
                {
                    dataQueryOperation = dataQueryOperation.TextSearch(filterDto.SearchTerm);
                }
            }
            else if (filterDto.AttributePaths != null && filterDto.SearchTerm != null)
            {
                dataQueryOperation =
                    dataQueryOperation.AttributeSearch(filterDto.AttributePaths, filterDto.SearchTerm);
            }
        }

        if (ctx.TryGetArgument(Statics.FieldFilterArg, out IEnumerable<FieldFilterDto>? fieldFilterDtoList))
        {
            foreach (var fieldFilterDto in fieldFilterDtoList)
            {
                dataQueryOperation = dataQueryOperation.FieldFilter(fieldFilterDto.AttributePath,
                    (FieldFilterOperator)fieldFilterDto.Operator, fieldFilterDto.ComparisonValue);
            }
        }

        if (ctx.TryGetArgument(Statics.SortOrderArg, out IEnumerable<SortDto>? sortDtos))
        {
            foreach (var sortDto in sortDtos)
            {
                dataQueryOperation = dataQueryOperation.SortOrder(sortDto.AttributePath,
                    (SortOrders)sortDto.SortOrder);
            }
        }

        if (ctx.TryGetArgument(Statics.AggregationsArg, out ResultAggregationInputDto? resultAggregationInputDto))
        {
            GetFieldAggregation(resultAggregationInputDto.GroupBy, dataQueryOperation);

            var aggregateResult = dataQueryOperation.AggregateResult();
            if (resultAggregationInputDto.CountAttributePaths != null)
            {
                aggregateResult.CountAttributePaths(resultAggregationInputDto.CountAttributePaths.ToArray());
            }

            if (resultAggregationInputDto.MaxValueAttributePaths != null)
            {
                aggregateResult.MaxAttributePaths(resultAggregationInputDto.MaxValueAttributePaths.ToArray());
            }

            if (resultAggregationInputDto.MinValueAttributePaths != null)
            {
                aggregateResult.MinAttributePaths(resultAggregationInputDto.MinValueAttributePaths.ToArray());
            }

            if (resultAggregationInputDto.AvgAttributePaths != null)
            {
                aggregateResult.AvgAttributePaths(resultAggregationInputDto.AvgAttributePaths.ToArray());
            }

            if (resultAggregationInputDto.SumAttributePaths != null)
            {
                aggregateResult.SumAttributePaths(resultAggregationInputDto.SumAttributePaths.ToArray());
            }
        }
        
        if (ctx.TryGetArgument(Statics.GeoNearFilterArg, out NearGeospatialFilterDto? nearGeospatialFilterDto))
        {
            var point = new Point(new Position(nearGeospatialFilterDto.Point.Coordinates.Latitude,
                nearGeospatialFilterDto.Point.Coordinates.Longitude));
            dataQueryOperation.NearGeospatialFilter(nearGeospatialFilterDto.AttributeName, point,
                nearGeospatialFilterDto.MinDistance, nearGeospatialFilterDto.MaxDistance);
        }

        return dataQueryOperation;
    }

    private static void GetFieldAggregation(FieldGroupByAggregationInputDto? fieldAggregationInputDto,
        DataQueryOperation dataQueryOperation)
    {
        if (fieldAggregationInputDto != null)
        {
            var aggregateFieldGroupBy =
                dataQueryOperation.AggregateFieldGroupBy(fieldAggregationInputDto.GroupByAttributePaths.ToArray());
            if (fieldAggregationInputDto.CountAttributePaths != null)
            {
                aggregateFieldGroupBy.CountAttributePaths(fieldAggregationInputDto.CountAttributePaths.ToArray());
            }

            if (fieldAggregationInputDto.MaxValueAttributePaths != null)
            {
                aggregateFieldGroupBy.MaxAttributePaths(fieldAggregationInputDto.MaxValueAttributePaths.ToArray());
            }

            if (fieldAggregationInputDto.MinValueAttributePaths != null)
            {
                aggregateFieldGroupBy.MinAttributePaths(fieldAggregationInputDto.MinValueAttributePaths.ToArray());
            }

            if (fieldAggregationInputDto.AvgAttributePaths != null)
            {
                aggregateFieldGroupBy.AvgAttributePaths(fieldAggregationInputDto.AvgAttributePaths.ToArray());
            }

            if (fieldAggregationInputDto.SumAttributePaths != null)
            {
                aggregateFieldGroupBy.SumAttributePaths(fieldAggregationInputDto.SumAttributePaths.ToArray());
            }
        }
    }
}