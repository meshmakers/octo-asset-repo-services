using Meshmakers.Octo.ConstructionKit.Contracts;

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

    public static Exception QueryNotFound(OctoObjectId queryRtId)
    {
        return new AssetRepositoryException($"Query with id '{queryRtId}' not found");
    }

    public static Exception AttributeNotFound(string attributePath, CkId<CkTypeId> ckTypeId)
    {
        return new AssetRepositoryException($"Attribute '{attributePath}' not found in type '{ckTypeId}'");
    }

    public static Exception NavigationWithoutRestrictionNotAllowed(CkId<CkAssociationRoleId> navigationPairCkRoleId,
        GraphDirections navigationPairDirection, CkId<CkTypeId> navigationPairTargetCkTypeId)
    {
        return new AssetRepositoryException(
            $"Navigation without restriction is not allowed for role id '{navigationPairCkRoleId}' in direction '{navigationPairDirection}' to '{navigationPairTargetCkTypeId}'");
    }

    public static Exception CannotConvertValue(object o, Type type)
    {
        return new AssetRepositoryException($"Cannot convert value '{o}' to type '{type}'");
    }

    public static Exception CannotConvertValueToString(object o)
    {
        return CannotConvertValue(o, typeof(string));
    }
}