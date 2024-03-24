namespace Meshmakers.Octo.Backend.AssetRepositoryServices;

internal class AssetRepositoryException : Exception
{
    public AssetRepositoryException()
    {
    }

    public AssetRepositoryException(string message) : base(message)
    {
    }

    public AssetRepositoryException(string message, Exception inner) : base(message, inner)
    {
    }

    internal static Exception ServiceNotRegistered(Type type)
    {
        return new AssetRepositoryException($"Service {type.FullName} is not registered");
    }

    internal static Exception CkIdMetadataMissing()
    {
        return new AssetRepositoryException("CkId metadata is missing");
    }

    internal static Exception DataLoaderContextUnavailable()
    {
        return new AssetRepositoryException("DataLoaderContext is not available");
    }
    
    internal static Exception SessionUnavailable()
    {
        return new AssetRepositoryException("Session is not available");
    }
}