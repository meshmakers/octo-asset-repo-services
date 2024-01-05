using System.Diagnostics.CodeAnalysis;
using GraphQL;
using GraphQL.Builders;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
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

    internal static DataQueryOperation GetDataQueryOperation<TEntity>(this IResolveConnectionContext<TEntity> ctx)
    {
        var dataQueryOperation = DataQueryOperation.Create();

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
            else if (filterDto.AttributeNames != null && filterDto.SearchTerm != null)
            {
                dataQueryOperation =
                    dataQueryOperation.AttributeSearch(filterDto.AttributeNames.Select(TransformAttributeName), filterDto.SearchTerm);
            }
        }

        if (ctx.TryGetArgument(Statics.FieldFilterArg, out IEnumerable<FieldFilterDto>? fieldFilterDtoList))
        {
            foreach (var fieldFilterDto in fieldFilterDtoList)
            {
                dataQueryOperation = dataQueryOperation.FieldFilter(TransformAttributeName(fieldFilterDto.AttributeName),
                    (FieldFilterOperator)fieldFilterDto.Operator, fieldFilterDto.ComparisonValue);
            }
        }

        if (ctx.TryGetArgument(Statics.SortOrderArg, out IEnumerable<SortDto>? sortDtos))
        {
            foreach (var sortDto in sortDtos)
            {
                dataQueryOperation = dataQueryOperation.SortOrder(TransformAttributeName(sortDto.AttributeName),
                    (SortOrders)sortDto.SortOrder);
            }
        }

        if (ctx.TryGetArgument(Statics.GroupByArg, out FieldGroupBy? groupByDto))
        {
            var groupBy = dataQueryOperation.GroupBy(groupByDto.GroupByAttributeNameList.Select(TransformAttributeName).ToArray());
            groupBy.CountAttributeNames(groupByDto.CountAttributeNameList.Select(TransformAttributeName).ToArray());
            groupBy.MaxAttributeNames(groupByDto.MaxValueAttributeNameList.Select(TransformAttributeName).ToArray());
            groupBy.MinAttributeNames(groupByDto.MinValueAttributeNameList.Select(TransformAttributeName).ToArray());
            groupBy.AvgAttributeNames(groupByDto.AvgAttributeNameList.Select(TransformAttributeName).ToArray());
        }

        return dataQueryOperation;
    }

    private static string TransformAttributeName(string attributeNameDto)
    {
        var attributeName = StringExtensions.ToPascalCase(attributeNameDto);

        return attributeName;
    }
}