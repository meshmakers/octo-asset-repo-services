using System.Collections.Generic;
using System.Linq;
using GraphQL;
using GraphQL.Builders;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
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

    internal static bool TryGetArgument<TType>(this IResolveFieldContext context, string name,
        out bool hasArgumentDefined, out TType value)
    {
        return TryGetArgument(context, name, default, out hasArgumentDefined, out value);
    }

    internal static bool TryGetArgument<TType>(this IResolveFieldContext context, string name, TType defaultValue,
        out bool hasArgumentDefined, out TType value)
    {
        hasArgumentDefined = false;
        if (context.HasArgument(name))
        {
            hasArgumentDefined = true;
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
        var dataQueryOperation = new DataQueryOperation();

        if (ctx.TryGetArgument(Statics.SearchFilterArg, out _, out SearchFilterDto filterDto))
        {
            if (filterDto.Type == null || filterDto.Type == SearchFilterTypesDto.TextSearch)
            {
                ArgumentValidation.ValidateString(nameof(filterDto.Language), filterDto.Language);

                dataQueryOperation.Language = filterDto.Language;
                dataQueryOperation.TextSearchFilter = new TextSearchFilter(filterDto.SearchTerm);
            }
            else
            {
                dataQueryOperation.AttributeSearchFilter =
                    new AttributeSearchFilter(filterDto.AttributeNames?.Select(TransformAttributeName),
                        filterDto.SearchTerm);
            }
        }

        if (ctx.TryGetArgument(Statics.FieldFilterArg, out _, out IEnumerable<FieldFilterDto> fieldFilterDtoList))
        {
            dataQueryOperation.FieldFilters = fieldFilterDtoList.Select(dto =>
                new FieldFilter(TransformAttributeName(dto.AttributeName), (FieldFilterOperator)dto.Operator,
                    dto.ComparisonValue));
        }

        if (ctx.TryGetArgument(Statics.SortOrderArg, out _, out IEnumerable<SortDto> sortDtos))
        {
            dataQueryOperation.SortOrders = sortDtos.Select(dto =>
                new SortOrderItem(StringExtensions.ToPascalCase(dto.AttributeName), (SortOrders)dto.SortOrder));
        }

        return dataQueryOperation;
    }

    private static string TransformAttributeName(string attributeNameDto)
    {
        var attributeName = StringExtensions.ToPascalCase(attributeNameDto);

        return attributeName;
    }
}
