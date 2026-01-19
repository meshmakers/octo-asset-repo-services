using System.Collections;
using System.Reflection;
using System.Text.Json.Serialization;
using GraphQL;
using GraphQL.Builders;
using GraphQL.Resolvers;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using ObjectExtensions = GraphQL.ObjectExtensions;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

internal static class Helpers
{
    internal static ITenantContext GetTenantContext(IDictionary<string, object?> context)
    {
        ITenantContext? tenantContext = null;
        if (context is GraphQlUserContext userContext)
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


    internal static FieldType Field<TSourceType>(this ComplexGraphType<TSourceType> complexGraphType,
        string name,
        string? description = null,
        IGraphType? graphType = null,
        QueryArguments? arguments = null,
        Func<IResolveFieldContext<TSourceType>, object?>? resolve = null,
        string? deprecationReason = null)
    {
        return complexGraphType.AddField(new FieldType
        {
            Name = name,
            Description = description,
            DeprecationReason = deprecationReason,
            ResolvedType = graphType,
            Arguments = arguments,
            Resolver = resolve != null ? new FuncFieldResolver<TSourceType, object>(resolve) : null
        });
    }

    internal static FieldType FieldAsync<TSourceType>(this ComplexGraphType<TSourceType> complexGraphType,
        string name,
        string? description = null,
        IGraphType? graphType = null,
        QueryArguments? arguments = null,
        Func<IResolveFieldContext<TSourceType>, ValueTask<object?>>? resolve = null,
        string? deprecationReason = null)
    {
        return complexGraphType.AddField(new FieldType
        {
            Name = name,
            Description = description,
            DeprecationReason = deprecationReason,
            ResolvedType = graphType,
            Arguments = arguments,
            Resolver = resolve != null ? new FuncFieldResolver<TSourceType, object>(resolve) : null
        });
    }

    internal static FieldType FieldAsyncByType<TSourceType>(this ComplexGraphType<TSourceType> complexGraphType,
        string name,
        string? description = null,
        Type? graphType = null,
        QueryArguments? arguments = null,
        Func<IResolveFieldContext<TSourceType>, ValueTask<object?>>? resolve = null,
        string? deprecationReason = null)
    {
        return complexGraphType.AddField(new FieldType
        {
            Name = name,
            Description = description,
            DeprecationReason = deprecationReason,
            Type = graphType,
            Arguments = arguments,
            Resolver = resolve != null ? new FuncFieldResolver<TSourceType, object>(resolve) : null
        });
    }

    public static ConnectionBuilder<TSourceType> Connection<TNodeType, TGraphType, TSourceType>(
        this ComplexGraphType<TNodeType> complexGraphType, IGraphTypesCache graphTypesCache, TGraphType itemType,
        string connectionName)
        where TGraphType : IGraphType
    {
        var type = graphTypesCache.GetOrCreateConnection(itemType);

        var connectionBuilder = ConnectionBuilder<TSourceType>.Create<TGraphType>(connectionName);
        connectionBuilder.FieldType.ResolvedType = type;
        complexGraphType.AddField(connectionBuilder.FieldType);
        return connectionBuilder;
    }

    public static ConnectionBuilder<TSourceType> AssociationField<TSourceType>(
        this ComplexGraphType<TSourceType> complexGraphType,
        IGraphTypesCache graphTypesCache, string name, IReadOnlyList<RtCkId<CkTypeId>> allowedTypes,
        RtCkId<CkTypeId> originCkId, RtCkId<CkTypeId> queryBaseType,
        RtCkId<CkAssociationRoleId> roleId, GraphDirections graphDirection)
    {
        DynamicConnectionType connectionType;

        // Check if there's a cached connection type from an interface (for inherited associations)
        // This ensures implementing types use the same connection type as the interface
        // IMPORTANT: We must also check that the cached connection has the same allowedTypes,
        // because different types may have associations with the same queryBaseType and name
        // but different target types (e.g., VehicleReading->parent->Vehicle vs Vehicle->parent->OperatingFacility)
        if (graphTypesCache.TryGetInterfaceAssociationConnection(queryBaseType, name, allowedTypes, out var cachedConnectionType) &&
            cachedConnectionType != null)
        {
            connectionType = cachedConnectionType;
        }
        else
        {
            var graphTypes = allowedTypes.Select(graphTypesCache.GetType).ToList();

            // Create a union type for all allowed target types
            // Use queryBaseType for the name since it describes the target types (e.g., Vehicle -> Car, Truck)
            // This makes the union name consistent regardless of which type creates it first
            var unionTypeName = $"{queryBaseType.GetGraphQlPascalCaseName()}_{name}{Statics.GraphQlUnionSuffix}";
            var unionType = new RtEntityUnionType(
                unionTypeName,
                $"Union of types derived from {queryBaseType.SemanticVersionedFullName} for {name} association",
                graphTypes);

            // Create connection type for the union
            connectionType = graphTypesCache.GetOrCreateConnection(unionType);
        }

        // Create a single connection field with ckTypeId filter argument
        var connectionBuilder = ConnectionBuilder<TSourceType>.Create<RtEntityUnionType>(name);
        connectionBuilder.FieldType.ResolvedType = connectionType;

        // Add metadata for resolver
        connectionBuilder.FieldType.Metadata[Statics.OriginCkId] = originCkId;
        connectionBuilder.FieldType.Metadata[Statics.RoleId] = roleId;
        connectionBuilder.FieldType.Metadata[Statics.GraphDirection] = graphDirection;
        connectionBuilder.FieldType.Metadata[Statics.AllowedTypes] = allowedTypes;
        connectionBuilder.FieldType.Metadata[Statics.QueryBaseType] = queryBaseType;

        complexGraphType.AddField(connectionBuilder.FieldType);
        return connectionBuilder;
    }

    public static ConnectionBuilder<TSourceType> AddMetadata<TSourceType>(
        this ConnectionBuilder<TSourceType> builder,
        string key, object value)
    {
        builder.FieldType.Metadata.Add(key, value);
        return builder;
    }

    /// <summary>
    ///     Adds an association field to an interface type.
    ///     Similar to AssociationField but without resolvers (interfaces don't have resolvers).
    ///     The connection type is cached so implementing types can reuse it.
    /// </summary>
    /// <param name="interfaceGraphType">The interface type to add the field to</param>
    /// <param name="graphTypesCache">The graph types cache</param>
    /// <param name="name">The field name (navigation property name)</param>
    /// <param name="allowedTypes">The concrete types that can be returned</param>
    /// <param name="baseCkTypeId">The CK type ID used for cache key (origin for outbound, target for inbound)</param>
    /// <param name="queryBaseType">The target type of the association (used for union naming)</param>
    public static FieldType InterfaceAssociationField<TSourceType>(
        this InterfaceGraphType<TSourceType> interfaceGraphType,
        IGraphTypesCache graphTypesCache, string name, IReadOnlyList<RtCkId<CkTypeId>> allowedTypes,
        RtCkId<CkTypeId> baseCkTypeId, RtCkId<CkTypeId> queryBaseType)
    {
        // Get or create the connection type, caching it so implementing types can reuse it
        var connectionType = graphTypesCache.GetOrCreateInterfaceAssociationConnection(
            baseCkTypeId, name, () =>
            {
                var graphTypes = allowedTypes.Select(graphTypesCache.GetType).ToList();

                // Create a union type for all allowed target types
                // Use queryBaseType for the name since it describes the target types
                var unionTypeName = $"{queryBaseType.GetGraphQlPascalCaseName()}_{name}{Statics.GraphQlUnionSuffix}";
                var unionType = new RtEntityUnionType(
                    unionTypeName,
                    $"Union of types derived from {queryBaseType.SemanticVersionedFullName} for {name} association",
                    graphTypes);

                // Create connection type for the union
                return graphTypesCache.GetOrCreateConnection(unionType);
            });

        // Add field to interface without a resolver (implementing types provide resolution)
        // Note: Only add 'first' and 'after' arguments, as these are the default arguments
        // that ConnectionBuilder creates. 'last' and 'before' require .Bidirectional() which
        // our implementing types don't use.
        var fieldType = new FieldType
        {
            Name = name,
            ResolvedType = connectionType,
            Arguments = new QueryArguments(
                new QueryArgument<IntGraphType> { Name = "first", Description = "Returns the first n elements from the list." },
                new QueryArgument<StringGraphType> { Name = "after", Description = "Returns the elements in the list that come after the specified cursor." }
            )
        };

        interfaceGraphType.AddField(fieldType);
        return fieldType;
    }

    public static FieldBuilder<TSourceType, TReturnType> Metadata<TSourceType, TReturnType>(
        this FieldBuilder<TSourceType, TReturnType> builder,
        string key, object value)

    {
        builder.FieldType.Metadata.Add(key, value);
        return builder;
    }

    public static FieldType AddMetadata(this FieldType fieldType,
        string key, object value)
    {
        fieldType.Metadata.Add(key, value);
        return fieldType;
    }


    public static T ToObjectWithWithUnknownProperties<T>(this IDictionary<string, object?> source,
        out Dictionary<string, object?> unmappedDictionary)
        where T : class, new()
    {
        return (T)source.ToObjectWithWithUnknownProperties(typeof(T), out unmappedDictionary);
    }

    /// <summary>
    ///     Creates a new instance of the indicated type, populating it with the dictionary.
    /// </summary>
    /// <param name="source">The source of values.</param>
    /// <param name="type">The type to create.</param>
    /// <param name="unmappedDictionary">The dictionary of values that were not mapped to properties.</param>
    private static object ToObjectWithWithUnknownProperties(this IDictionary<string, object?> source, Type type,
        out Dictionary<string, object?> unmappedDictionary)
    {
        var obj = Activator.CreateInstance(type);
        if (obj == null)
        {
            throw new InvalidOperationException($"Could not create instance of type {type.FullName}");
        }

        Dictionary<string, object?>? extensionDataDictionary = null;
        unmappedDictionary = new Dictionary<string, object?>();

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
                if (extensionDataDictionary == null)
                {
                    var dictionaryType = type.GetProperties().FirstOrDefault(prop =>
                        Attribute.IsDefined(prop, typeof(JsonExtensionDataAttribute)));

                    if (dictionaryType != null)
                    {
                        extensionDataDictionary = new Dictionary<string, object?>();
                        dictionaryType.SetValue(obj, extensionDataDictionary, null);
                    }
                }

                if (extensionDataDictionary != null)
                {
                    extensionDataDictionary.Add(item.Key.ToPascalCase(), item.Value);
                }
                else
                {
                    unmappedDictionary.Add(item.Key, item.Value);
                }
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
    private static object? GetPropertyValue2(this object? propertyValue, Type fieldType)
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
                var list = Activator.CreateInstance(fieldType);
                if (list == null || !(list is IList myList))
                {
                    throw new InvalidOperationException($"Could not create instance of type {fieldType.FullName}");
                }

                newArray = myList;
            }
            else
            {
                var genericListType = typeof(List<>).MakeGenericType(elementType);
                var list = Activator.CreateInstance(genericListType);

                if (list == null || !(list is IList myList))
                {
                    throw new InvalidOperationException($"Could not create instance of type {fieldType.FullName}");
                }

                newArray = myList;
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

        if (propertyValue is Dictionary<string, object?> objects)
        {
            return ToObjectWithWithUnknownProperties(objects, fieldType, out _);
        }

        if (fieldType.GetTypeInfo().IsEnum)
        {
            if (value == null)
            {
                var enumNames = Enum.GetNames(fieldType);
                value = enumNames[0];
            }

            if (!ObjectExtensions.IsDefinedEnumValue(fieldType, value))
            {
                throw new ExecutionError($"Unknown value '{value}' for enum '{fieldType.Name}'.");
            }

            var str = value.ToString();
            if (!string.IsNullOrWhiteSpace(str))
            {
                value = Enum.Parse(fieldType, str, true);
            }
        }

        return ConvertValue(value, fieldType);
    }

    private static object? ConvertValue(object? value, Type targetType)
    {
        return ValueConverter.ConvertTo(value, targetType);
    }
}