using System.Diagnostics.CodeAnalysis;
using GraphQL;
using GraphQL.Builders;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Geospatial.Geometry;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Services.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

internal static class ResolveConnectionContextExtensions
{
    internal static ICkCacheService GetCkCacheService(this IResolveFieldContext context)
    {
        var ckCacheService = context.RequestServices?.GetRequiredService<ICkCacheService>();
        if (ckCacheService == null)
        {
            throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
        }

        return ckCacheService;
    }

    internal static IStreamDataDatabaseClient GetStreamDataDatabaseClient(this IResolveFieldContext context)
    {
        var streamDataDatabaseClient = context.RequestServices?.GetRequiredService<IStreamDataDatabaseClient>();
        if (streamDataDatabaseClient == null)
        {
            throw AssetRepositoryException.ServiceNotRegistered(typeof(IStreamDataDatabaseClient));
        }

        return streamDataDatabaseClient;
    }

    internal static IOctoSessionAccessor GetSessionAccessor(this IResolveFieldContext context)
    {
        var sessionAccessor = context.RequestServices?.GetRequiredService<IOctoSessionAccessor>();
        if (sessionAccessor?.Session == null)
        {
            throw AssetRepositoryException.SessionUnavailable();
        }

        return sessionAccessor;
    }

    internal static T GetMetadataValue<T>(this IResolveFieldContext context, string name)
    {
        if (!context.FieldDefinition.Metadata.TryGetValue(Statics.CkId, out var valueString))
        {
            throw AssetRepositoryException.CkIdMetadataMissing(name);
        }

        if (valueString is not T value)
        {
            throw AssetRepositoryException.CkIdMetadataInvalidType(name, typeof(T));
        }

        return value;
    }

    internal static object? HandleException(this IResolveFieldContext context, Exception exception)
    {
        if (exception is PersistenceException persistenceException)
        {
            context.Errors.Add(new ExecutionError(persistenceException.Message, persistenceException)
                { Code = Statics.GraphQlErrorDataStore });
        }
        else if (exception is NavigationPropertyException navigationPropertyAssignException)
        {
            var error = new ExecutionError(navigationPropertyAssignException.Message,
                navigationPropertyAssignException)
            {
                Code = Statics.GraphQlNavigationPropertyError,
                Extensions = new Dictionary<string, object?>()
            };

            error.Extensions[Statics.GraphQlDetails] = navigationPropertyAssignException.DetailMessage;
            context.Errors.Add(error);
        }
        else
        {
            context.Errors.Add(new ExecutionError("An error occurred", exception)
                { Code = Statics.GraphQlErrorCommon });
        }

        return null;
    }

    internal static void ValidateOperationResult(this IResolveFieldContext context, OperationResult operationResult)
    {
        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            foreach (var message in operationResult.Messages)
            {
                if (message.MessageLevel == MessageLevel.Error)
                {
                    context.Errors.Add(new ExecutionError(message.MessageText)
                        { Code = string.Format(Statics.GraphQlModelValidationError, message.MessageNumber) });
                }
                else if (message.MessageLevel == MessageLevel.FatalError)
                {
                    context.Errors.Add(new ExecutionError(message.MessageText)
                        { Code = string.Format(Statics.GraphQlModelValidationFatalError, message.MessageNumber) });
                }
            }

            throw new ExecutionError("Operation failed. See errors for details.")
                { Code = Statics.GraphQlModelValidationErrors };
        }
    }

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
        [NotNullWhen(true)] out TType? value)
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

    internal static DataQueryOperation GetDataQueryOperation<TEntity>(this IResolveConnectionContext<TEntity> ctx,
        DataQueryOperation? operation = null)
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