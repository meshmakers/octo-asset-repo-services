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
    internal const string RtCkIdArg = "rtCkId";
    internal const string ColumnPathsArg = "columnPaths";
    internal const string GroupByColumnPathsArg = "groupByColumnPaths";
    internal const string CkIdsArg = "ckIds";
    internal const string RtCkIdsArg = "rtCkIds";
    internal const string RoleIdArg = "roleId";
    internal const string IncludeIndirectArg = "includeIndirect";
    internal const string DirectionArg = "direction";
    internal const string RelatedRtCkId = "relatedRtCkId";
    internal const string RelatedRtId = "relatedRtId";
    internal const string ValuesArg = "values";
    internal const string OptionsArg = "options";

    internal const string UpdateTypesArg = "updateTypes";
    internal const string SearchFilterArg = "searchFilter";
    internal const string AttributePathContainsFilterArg = "attributePathContains";
    internal const string AttributeNameContainsFilterArg = "attributeNameContains";
    internal const string IgnoreAbstractTypesArg = "ignoreAbstractTypes";
    internal const string IncludeSelfArgs = "includeSelf";
    internal const string AttributeNamesFilterArg = "attributeNames";
    internal const string ResolveEnumValuesToNames = "resolveEnumValuesToNames";
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

    internal const string AggregationsArg = "aggregations";

    public const string GraphQlConnectionSuffix = "Connection";
    public const string GraphQlEdgeSuffix = "Edge";
    public const string GraphQlUnionSuffix = "Union";
    public const string GraphQlUpdateSuffix = "Update";
    public const string GraphQlInputSuffix = "Input";
    public const string GraphQlUpdateMessageSuffix = "Message";
    public const string GraphQlUpdatePrefix = "Update";

    public const string GraphQlDetails = "OctoDetails";
    public const string GraphQlInvalidArguments = "ASSET1000";
    public const string GraphQlErrorDataStore = "ASSET1001";
    public const string GraphQlErrorCommon = "ASSET1002";
    public const string GraphQlDeleteOperationsNotSupported = "ASSET1003";
    public const string GraphQlModelValidationErrors = "ASSET1004";
    public const string GraphQlErrorCache = "ASSET1005";
    public const string GraphQlCkModelUpdateError = "ASSET1006";

    public static string GetGraphQlPascalCaseName<TKey>(this RtCkId<TKey> ckKey) where TKey : IComparable<TKey>, ICkElementId
    {
        return ckKey.SemanticVersionedFullName
            .Replace(".", "")
            .Replace("/", "")
            .ToPascalCase();
    }

    public static string GetGraphQlCamelCaseName<TKey>(this RtCkId<TKey> ckKey) where TKey : IComparable<TKey>, ICkElementId
    {
        return ckKey.SemanticVersionedFullName
            .Replace(".", "")
            .Replace("/", "")
            .ToCamelCase();
    }

    public static string GetGraphQlPascalCaseNameForStreamData<TKey>(this RtCkId<TKey> ckKey)
        where TKey : IComparable<TKey>, ICkElementId
    {
        return "stream" + ckKey.SemanticVersionedFullName
            .Replace(".", "")
            .Replace("/", "")
            .ToPascalCase();
    }
}