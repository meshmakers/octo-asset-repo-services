using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

internal static class Statics
{
    internal const string CkId = "CkId";
    internal const string EntitiesArg = "entities";
    internal const string RtIdArg = "rtId";
    internal const string RtIdsArg = "rtIds";
    internal const string CkModelIds = "ckModelIds";
    internal const string CkIdArg = "ckId";
    internal const string CkIdsArg = "ckIds";
    internal const string RoleIdArg = "roleId";
    internal const string IncludeIndirectArg = "includeIndirect";
    internal const string DirectionArg = "direction";
    internal const string ValuesArg = "values";

    internal const string UpdateTypesArg = "updateTypes";
    internal const string SearchFilterArg = "searchFilter";
    internal const string AttributeNameContainsFilterArg = "attributeNameContains";
    internal const string AttributeNamesFilterArg = "attributeNames";
    internal const string AttributePathsFilterArg = "attributePaths";
    internal const string FieldBeforeFilterArg = "beforeFieldFilter";
    internal const string FieldFilterArg = "fieldFilter";
    internal const string GeoNearFilterArg = "geoNearFilter";
    internal const string SortOrderArg = "sortOrder";
    internal const string AttributeGraphType = "AttributeGraphType";
    internal const string TypeGraphType = "TypeGraphType";
    internal const string StreamDataArgument = "arg";
    internal const string StreamDataAttributeArgument = "arg";
    internal const string ItemsQueryArg = "items";

    internal const string LargeBinaryIdArg = "largeBinaryId";
    internal const string LargeBinaryDataArg = "binaryData";
    internal const string GroupByArg = "groupBy";
    
    public const string GraphQlConnectionSuffix = "Connection";
    public const string GraphQlEdgeSuffix = "Edge";
    public const string GraphQlUnionSuffix = "Union";
    public const string GraphQlUpdateSuffix = "Update";
    public const string GraphQlInputSuffix = "Input";
    public const string GraphQlUpdateMessageSuffix = "Message";
    public const string GraphQlDeletePrefix = "DeleteInput";
    public const string GraphQlUpdatePrefix = "Update";
    public const string GraphQlCreationPrefix = "Creation";

    public const string GraphQLErrorNotFound = "OCTO1000";
    public const string GraphQLErrorConflict = "OCTO1001";
    public const string GraphQLErrorInvalidType = "OCTO1002";
    public const string GraphQLErrorDataStore = "OCTO1003";
    public const string GraphQLErrorCommon = "OCTO1004";
    public const string GraphQLDeleteOperationsNotSupported = "OCTO1005";
    public const string GraphQLOperationError = "OCTO1006_{0}";
    public const string GraphQLOperationFatalError = "OCTO1007_{0}";

    public const string GraphQlStreamDataQueryError = "OCT01008";

    public static string GetGraphQlPascalCaseName<TKey>(this CkId<TKey> ckKey) where TKey : IComparable<TKey>, ICkKey
    {
        return ckKey.SemanticVersionedFullName
            .Replace(".", "")
            .Replace("/", "")
            .ToPascalCase();
    }
    
    public static string GetGraphQlCamelCaseName<TKey>(this CkId<TKey> ckKey) where TKey : IComparable<TKey>, ICkKey
    {
        return ckKey.SemanticVersionedFullName
            .Replace(".", "")
            .Replace("/", "")
            .ToCamelCase();
    }
    
    public static string GetGraphQlPascalCaseNameForStreamData<TKey>(this CkId<TKey> ckKey) where TKey : IComparable<TKey>, ICkKey
    {
        return "stream" + ckKey.SemanticVersionedFullName
            .Replace(".", "")
            .Replace("/", "")
            .ToPascalCase();
    }
}