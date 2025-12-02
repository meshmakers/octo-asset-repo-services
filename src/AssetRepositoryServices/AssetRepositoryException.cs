using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices;

internal class AssetRepositoryException : Exception
{
    public class DetailMessage
    {
        // ReSharper disable once UnusedAutoPropertyAccessor.Global
        public required string Message { get; init; }

        // ReSharper disable once CollectionNeverQueried.Global
        public List<string> Details { get; } = new();
    }

    public AssetRepositoryException()
    {
        Details = new List<DetailMessage>();
    }

    public AssetRepositoryException(string message) : base(message)
    {
        Details = new List<DetailMessage>();
    }

    public AssetRepositoryException(string message, Exception inner) : base(message, inner)
    {
        Details = new List<DetailMessage>();
    }

    // ReSharper disable once CollectionNeverQueried.Global
    public List<DetailMessage> Details { get; }

    internal static Exception ServiceNotRegistered(Type type)
    {
        return new AssetRepositoryException($"Service {type.FullName} is not registered");
    }

    internal static Exception CkIdMetadataMissing(string name)
    {
        return new AssetRepositoryException($"{name} metadata is missing");
    }

    internal static Exception DataLoaderContextUnavailable()
    {
        return new AssetRepositoryException("DataLoaderContext is not available");
    }

    internal static Exception SessionUnavailable()
    {
        return new AssetRepositoryException("Session is not available");
    }

    internal static Exception RequestServicesNotAvailable()
    {
        return new AssetRepositoryException("RequestServices is not available");
    }

    public static Exception ParentUnavailable()
    {
        return new AssetRepositoryException("Parent is not available");
    }

    public static Exception ArgumentMissing(string name)
    {
        return new AssetRepositoryException($"Argument {name} is missing");
    }

    public static Exception QueryNotFound(OctoObjectId queryRtId)
    {
        return new AssetRepositoryException($"Query with id '{queryRtId}' not found");
    }


    public static Exception CkIdMetadataInvalidType(string name, Type type)
    {
        return new AssetRepositoryException($"{name} metadata is not of type {type.FullName}");
    }

    public static Exception AggregationResultNull()
    {
        return new AssetRepositoryException("Aggregation result is null");
    }

    public static Exception SourceNotSet()
    {
        return new AssetRepositoryException("Source is not set");
    }

    public static Exception UserContextNotSet()
    {
        return new AssetRepositoryException("User context is not set");
    }

    public static Exception InvalidColumnPaths(List<string> invalidColumnPaths)
    {
        return new AssetRepositoryException($"Invalid column paths: {string.Join(", ", invalidColumnPaths)}");
    }

    public static Exception RtQueryNotFound(OctoObjectId queryRtId)
    {
        return new AssetRepositoryException($"RtQuery with id '{queryRtId}' not found");
    }

    public static Exception InvalidStreamDataQueryParams()
    {
        return new AssetRepositoryException("Invalid query. From, To and Limit must be set for downsampling");
    }

    public static Exception OperationResultErrors(OperationResult operationResult)
    {
        var ex = new AssetRepositoryException("Execution was aborted due to an error. Please check the details.");
        foreach (var message in operationResult.Messages)
        {
            var detailMessage = new DetailMessage
            {
                Message = $"{message.MessageNumber}: {message.MessageText}"
            };
            ex.Details.Add(detailMessage);
        }

        return ex;
    }

    public static Exception InvalidArgumentsCkIdAndRtCkIdInSameQuery()
    {
        return new AssetRepositoryException("Invalid arguments: Cannot use both CkTypeId and RtCkTypeId in the same query");
    }

    public static Exception InvalidArgumentsCkIdOrRtCkIdAndModelIdInSameQuery()
    {
        return new AssetRepositoryException("Invalid arguments: Cannot use both ckId or rtCkId and ckModelIds in the same query");
    }
}