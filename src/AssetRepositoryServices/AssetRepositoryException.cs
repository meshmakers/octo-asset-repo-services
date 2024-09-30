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

    internal static Exception RequestServicesNotAvailable()
    {
        return new AssetRepositoryException("RequestServices is not available");
    }

    internal static Exception RoleIdMissing()
    {
        return new AssetRepositoryException("RoleId is missing");
    }

    internal static Exception DirectionMissing()
    {
        return new AssetRepositoryException("Direction is missing");
    }

    public static Exception ParentUnavailable()
    {
       return new AssetRepositoryException("Parent is not available");
    }

    public static Exception ArgumentMissing(string valuesArg)
    {
        return new AssetRepositoryException($"Argument {valuesArg} is missing");
    }
}