using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

internal static class Statics
{
    internal const string CkId = "CkId";
    internal const string RoleId = "RoleId";
    internal const string GraphDirection = "GraphDirection";
    internal const string EntitiesArg = "entities";
    internal const string RtIdArg = "rtId";
    internal const string RtIdsArg = "rtIds";
    internal const string CkIdArg = "ckId";
    internal const string CkIdsArg = "ckIds";
    internal const string UpdateTypesArg = "updateTypes";
    internal const string SearchFilterArg = "searchFilter";
    internal const string AttributeNamesFilterArg = "attributeNames";
    internal const string FieldFilterArg = "fieldFilter";
    internal const string SortOrderArg = "sortOrder";
    internal const string TenantContext = "tenantContext";
    internal const string AttributeGraphType = "AttributeGraphType";
    internal const string TypeGraphType = "TypeGraphType";

    internal const string LargeBinaryIdArg = "largeBinaryId";
    internal const string LargeBinaryDataArg = "binaryData";
    internal const string GroupByArg = "groupBy";

    public static string GetGraphQlName<TKey>(this CkId<TKey> ckKey) where TKey : struct, IComparable<TKey>, ICkKey
    {
        return ckKey.SemanticVersionedFullName
            .Replace(".", "")
            .Replace("/", "")
            .ToCamelCase();
    }
}