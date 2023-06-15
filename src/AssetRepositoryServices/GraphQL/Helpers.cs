using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using GraphQL;
using GraphQL.Builders;
using GraphQL.DataLoader;
using GraphQL.Resolvers;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Newtonsoft.Json;
using ObjectExtensions = GraphQL.ObjectExtensions;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

internal static class Helpers
{
    internal static ITenantContext GetTenantContext(IDictionary<string, object?> context)
    {
        ITenantContext tenantContext = null;
        if (context is GraphQLUserContext userContext)
        {
            tenantContext = userContext.TenantContext;
        }


        // Client tried to use an invalid tenant
        if (tenantContext == null)
        {
            throw new InvalidOperationException("Invalid request. Please check your client configuration.");
        }

        return tenantContext;
    }


    internal static FieldType Field<TSourceType>(this ComplexGraphType<TSourceType> _this,
        string name,
        string description = null,
        IGraphType graphType = null,
        QueryArguments arguments = null,
        Func<IResolveFieldContext<TSourceType>, object> resolve = null,
        string deprecationReason = null)
    {
        return _this.AddField(new FieldType
        {
            Name = name,
            Description = description,
            DeprecationReason = deprecationReason,
            ResolvedType = graphType,
            Arguments = arguments,
            Resolver = resolve != null ? new FuncFieldResolver<TSourceType, object>(resolve) : null
        });
    }

    internal static FieldType FieldAsync<TSourceType>(this ComplexGraphType<TSourceType> _this,
        string name,
        string description = null,
        IGraphType graphType = null,
        QueryArguments arguments = null,
        Func<IResolveFieldContext<TSourceType>, ValueTask<object>> resolve = null,
        string deprecationReason = null)
    {
        return _this.AddField(new FieldType
        {
            Name = name,
            Description = description,
            DeprecationReason = deprecationReason,
            ResolvedType = graphType,
            Arguments = arguments,
            Resolver = resolve != null ? new FuncFieldResolver<TSourceType, object>(resolve) : null
        });
    }

    public static ConnectionBuilder<TSourceType> Connection<TNodeType, TGraphType, TSourceType>(
        this ComplexGraphType<TNodeType> _this, IGraphTypesCache entityDtoCache, TGraphType itemType,
        string prefixName)
        where TGraphType : IGraphType
    {
        var type = entityDtoCache.GetOrCreateConnection(itemType, prefixName);

        var connectionBuilder =
            ConnectionBuilder<TSourceType>.Create<TGraphType>(
                $"{prefixName}{CommonConstants.GraphQlConnectionSuffix}");
        connectionBuilder.FieldType.ResolvedType = type;
        _this.AddField(connectionBuilder.FieldType);
        return connectionBuilder;
    }

    public static FieldType AssociationField<TSourceType>(
        this ComplexGraphType<TSourceType> _this,
        IGraphTypesCache graphTypesCache, IDataLoaderContextAccessor dataLoaderAccessor,
        IOctoSessionAccessor sessionAccessor, string name, IReadOnlyList<string> allowedTypes, string originCkId,
        string roleId, GraphDirections graphDirection)
    {
        var graphTypes = allowedTypes.Select(ckId => graphTypesCache.GetOrCreate(ckId));

        var unionType = new RtEntityAssociationType(
            $"{_this.Name}_{name}{CommonConstants.GraphQlUnionSuffix}",
            $"Association {roleId} ({graphDirection}) of entity type {_this.Name}", graphTypesCache, dataLoaderAccessor,
            sessionAccessor, graphTypes, originCkId, roleId, graphDirection);

        return _this.Field(name, null, unionType, resolve: context => context.Source);
    }

    public static ConnectionBuilder<TSourceType> AddMetadata<TSourceType>(
        this ConnectionBuilder<TSourceType> _this,
        string key, object value)
    {
        _this.FieldType.Metadata.Add(key, value);
        return _this;
    }

    public static FieldBuilder<TSourceType, TReturnType> Metadata<TSourceType, TReturnType>(
        this FieldBuilder<TSourceType, TReturnType> _this,
        string key, object value)

    {
        _this.FieldType.Metadata.Add(key, value);
        return _this;
    }

    public static FieldType AddMetadata(this FieldType _this,
        string key, object value)
    {
        _this.Metadata.Add(key, value);
        return _this;
    }

    public static string GetTenantId(this HttpContext _this)
    {
        return (string)_this.GetRouteValue("tenantId");
    }

    public static List<T> ToList<T>(this object source) where T : class, new()
    {
        if (source == null)
        {
            return null;
        }

        var result = new List<T>();

        if (source is IList<object> list)
        {
            foreach (var item in list)
            {
                if (item is Dictionary<string, object> dict)
                {
                    result.Add(dict.ToObjectWithWithUnknownProperties<T>());
                }
                else
                {
                    throw new NotSupportedException();
                }
            }
        }

        return result;
    }

    public static T ToObjectWithWithUnknownProperties<T>(this IDictionary<string, object> source)
        where T : class, new()
    {
        return (T)source.ToObjectWithWithUnknownProperties(typeof(T));
    }

    /// <summary>
    ///     Creates a new instance of the indicated type, populating it with the dictionary.
    /// </summary>
    /// <param name="source">The source of values.</param>
    /// <param name="type">The type to create.</param>
    public static object ToObjectWithWithUnknownProperties(this IDictionary<string, object> source, Type type)
    {
        var obj = Activator.CreateInstance(type);

        Dictionary<string, object> unknownPropertyDictionary = null;

        foreach (var item in source)
        {
            var propertyType = type.GetProperty(item.Key,
                BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            if (propertyType != null)
            {
                var value = item.Value.GetPropertyValue2(propertyType.PropertyType);
                propertyType.SetValue(obj, value, null);
            }
            else
            {
                if (unknownPropertyDictionary == null)
                {
                    var dictionaryType = type.GetProperties().FirstOrDefault(prop =>
                        Attribute.IsDefined(prop, typeof(JsonExtensionDataAttribute)));

                    if (dictionaryType != null)
                    {
                        unknownPropertyDictionary = new Dictionary<string, object>();
                        dictionaryType.SetValue(obj, unknownPropertyDictionary, null);
                    }
                }

                unknownPropertyDictionary?.Add(item.Key.ToPascalCase(), item.Value);
            }
        }

        return obj;
    }

    /// <summary>
    ///     Converts the indicated value into a type that is compatible with fieldType.
    /// </summary>
    /// <param name="propertyValue">The value to be converted.</param>
    /// <param name="fieldType">The desired type.</param>
    /// <remarks>There is special handling for strings, IEnumerable&lt;T&gt;, Nullable&lt;T&gt;, and Enum.</remarks>
    public static object GetPropertyValue2(this object propertyValue, Type fieldType)
    {
        // Short-circuit conversion if the property value already
        if (fieldType.IsInstanceOfType(propertyValue))
        {
            return propertyValue;
        }

        if (fieldType.FullName == "System.Object")
        {
            return propertyValue;
        }

        var enumerableInterface = fieldType.Name == "IEnumerable`1"
            ? fieldType
            : fieldType.GetInterface("IEnumerable`1");

        if (fieldType.Name != "String"
            && enumerableInterface != null)
        {
            IList newArray;
            var elementType = enumerableInterface.GetGenericArguments()[0];
            var underlyingType = Nullable.GetUnderlyingType(elementType) ?? elementType;
            var implementsIList = fieldType.GetInterface("IList") != null;

            if (implementsIList && !fieldType.IsArray)
            {
                newArray = (IList)Activator.CreateInstance(fieldType);
            }
            else
            {
                var genericListType = typeof(List<>).MakeGenericType(elementType);
                newArray = (IList)Activator.CreateInstance(genericListType);
            }

            if (!(propertyValue is IEnumerable valueList))
            {
                return newArray;
            }

            foreach (var listItem in valueList)
            {
                newArray.Add(listItem == null ? null : GetPropertyValue2(listItem, underlyingType));
            }

            if (fieldType.IsArray)
            {
                var array = Array.CreateInstance(elementType, newArray.Count);
                newArray.CopyTo(array, 0);
                return array;
            }

            return newArray;
        }

        var value = propertyValue;

        var nullableFieldType = Nullable.GetUnderlyingType(fieldType);

        // if this is a nullable type and the value is null, return null
        if (nullableFieldType != null && value == null)
        {
            return null;
        }

        if (nullableFieldType != null)
        {
            fieldType = nullableFieldType;
        }

        if (propertyValue is Dictionary<string, object> objects)
        {
            return ToObjectWithWithUnknownProperties(objects, fieldType);
        }

        if (fieldType.GetTypeInfo().IsEnum)
        {
            if (value == null)
            {
                var enumNames = Enum.GetNames(fieldType);
                value = enumNames[0];f
            }

            if (!ObjectExtensions.IsDefinedEnumValue(fieldType, value))
            {
                throw new ExecutionError($"Unknown value '{value}' for enum '{fieldType.Name}'.");
            }

            var str = value.ToString();
            value = Enum.Parse(fieldType, str, true);
        }

        return ConvertValue(value, fieldType);
    }

    private static object ConvertValue(object value, Type targetType)
    {
        return ValueConverter.ConvertTo(value, targetType);
    }
}
