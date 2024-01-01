using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

[Serializable]
public class OctoGraphQLException : Exception
{
    //
    // For guidelines regarding the creation of new exception types, see
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/cpgenref/html/cpconerrorraisinghandlingguidelines.asp
    // and
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/dncscol/html/csharp07192001.asp
    //

    public OctoGraphQLException()
    {
    }

    public OctoGraphQLException(string message) : base(message)
    {
    }

    public OctoGraphQLException(string message, Exception inner) : base(message, inner)
    {
    }

    internal static Exception AttributeValueTypeNotSupported(AttributeValueTypesDto valueType)
    {
        return new OctoGraphQLException($"Attribute value type {valueType} is not supported.");
    }

    public static Exception RecordAttributeHasNoCkRecordId(string attributeName)
    {
        return new OctoGraphQLException($"Record attribute {attributeName} has no CkRecordId.");
    }

    public static Exception AttributeNameMetadataNotFound(string fieldDefinitionName)
    {
        return new OctoGraphQLException($"Attribute name metadata not found for field definition {fieldDefinitionName}.");
    }

    public static Exception EnumAttributeHasNoCkEnumId(string attributeName)
    {
        return new OctoGraphQLException($"Enum attribute {attributeName} has no CkEnumId.");
    }
}
