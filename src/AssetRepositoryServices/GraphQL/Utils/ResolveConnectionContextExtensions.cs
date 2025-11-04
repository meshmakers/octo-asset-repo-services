using System.Diagnostics.CodeAnalysis;
using GraphQL;
using GraphQL.Builders;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Geospatial.Geometry;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
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
        if (exception is CkCacheException ckCacheException)
        {
            context.Errors.Add(new ExecutionError(ckCacheException.Message, ckCacheException)
                { Code = Statics.GraphQlErrorCache });
        }
        else if (exception is CkModelException ckModelException)
        {
            var error =new ExecutionError(ckModelException.Message, ckModelException)
            {
                Code = Statics.GraphQlCkModelUpdateError,
                Extensions = new Dictionary<string, object?>()
            };

            List<AssetRepositoryException.DetailMessage> details = new();
            var innerException = ckModelException.InnerException;
            while (innerException != null)
            {
                var detailMessage = new AssetRepositoryException.DetailMessage
                {
                    Message = $"{innerException.Message}"
                };
                details.Add(detailMessage);
                innerException = innerException.InnerException;
            }
            error.Extensions[Statics.GraphQlDetails] = details;

            context.Errors.Add(error);

        }
        else if (exception is RuntimeRepositoryException runtimeRepositoryException)
        {
            var error = new ExecutionError("Execution was aborted due to an error. Please check the details.",
                runtimeRepositoryException)
            {
                Code = Statics.GraphQlModelValidationErrors,
                Extensions = new Dictionary<string, object?>()
            };

            var operationResult = runtimeRepositoryException.OperationResult;
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                List<AssetRepositoryException.DetailMessage> details = new();
                foreach (var message in operationResult.Messages)
                {
                    var detailMessage = new AssetRepositoryException.DetailMessage
                    {
                        Message = $"{message.MessageNumber}: {message.MessageText}"
                    };
                    details.Add(detailMessage);
                }
                error.Extensions[Statics.GraphQlDetails] = details;
            }

            context.Errors.Add(error);
        }
        else if (exception is AssetRepositoryException assetRepositoryException)
        {
            var error = new ExecutionError(assetRepositoryException.Message,
                assetRepositoryException)
            {
                Code = Statics.GraphQlModelValidationErrors,
                Extensions = new Dictionary<string, object?>()
            };

            error.Extensions[Statics.GraphQlDetails] = assetRepositoryException.Details;
            context.Errors.Add(error);
        }
        else if (exception is PersistenceException persistenceException)
        {
            context.Errors.Add(new ExecutionError(persistenceException.Message, persistenceException)
                { Code = Statics.GraphQlErrorDataStore });
        }
        else
        {
            context.Errors.Add(new ExecutionError("An error occurred", exception)
                { Code = Statics.GraphQlErrorCommon });
        }

        return null;
    }

    internal static void ValidateOperationResult(OperationResult operationResult)
    {
        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            throw AssetRepositoryException.OperationResultErrors(operationResult);
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

    internal static RtEntityQueryOptions GetDataQueryOperation<TEntity>(this IResolveConnectionContext<TEntity> ctx,
        RtEntityQueryOptions? operation = null)
    {
        var dataQueryOperation = operation ?? RtEntityQueryOptions.Create();

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
        RtEntityQueryOptions queryOptions)
    {
        if (fieldAggregationInputDto != null)
        {
            var aggregateFieldGroupBy =
                queryOptions.AggregateFieldGroupBy(fieldAggregationInputDto.GroupByAttributePaths.ToArray());
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