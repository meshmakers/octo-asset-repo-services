using System.Collections;
using System.Reflection;
using GraphQL;
using GraphQL.Builders;
using GraphQL.DataLoader;
using GraphQL.Resolvers;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Newtonsoft.Json;
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

    public static ConnectionBuilder<TSourceType> Connection<TNodeType, TGraphType, TSourceType>(
        this ComplexGraphType<TNodeType> complexGraphType, IGraphTypesCache graphTypesCache, TGraphType itemType,
        string prefixName)
        where TGraphType : IGraphType
    {
        var type = graphTypesCache.GetOrCreateConnection(itemType, prefixName);

        var connectionBuilder =
            ConnectionBuilder<TSourceType>.Create<TGraphType>(
                $"{prefixName}");
        connectionBuilder.FieldType.ResolvedType = type;
        complexGraphType.AddField(connectionBuilder.FieldType);
        return connectionBuilder;
    }

    public static FieldType AssociationField<TSourceType>(
        this ComplexGraphType<TSourceType> complexGraphType,
        IGraphTypesCache graphTypesCache, IDataLoaderContextAccessor dataLoaderAccessor,
        IOctoSessionAccessor sessionAccessor, string name, IReadOnlyList<CkId<CkTypeId>> allowedTypes, CkId<CkTypeId> originCkId,
        CkId<CkAssociationRoleId> roleId, GraphDirections graphDirection)
    {
        var graphTypes = allowedTypes.Select(graphTypesCache.GetType);

        var unionType = new RtEntityAssociationType(
            $"{complexGraphType.Name}_{name}{Statics.GraphQlUnionSuffix}",
            $"Association {roleId} ({graphDirection}) of entity type {complexGraphType.Name}", graphTypesCache, dataLoaderAccessor,
            sessionAccessor, graphTypes, originCkId, roleId, graphDirection);

        return complexGraphType.Field(name, null, unionType, resolve: context => context.Source);
    }

    public static ConnectionBuilder<TSourceType> AddMetadata<TSourceType>(
        this ConnectionBuilder<TSourceType> builder,
        string key, object value)
    {
        builder.FieldType.Metadata.Add(key, value);
        return builder;
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


    public static T? ToObjectWithWithUnknownProperties<T>(this IDictionary<string, object?> source)
        where T : class, new()
    {
        return (T?)source.ToObjectWithWithUnknownProperties(typeof(T));
    }

    /// <summary>
    ///     Creates a new instance of the indicated type, populating it with the dictionary.
    /// </summary>
    /// <param name="source">The source of values.</param>
    /// <param name="type">The type to create.</param>
    private static object? ToObjectWithWithUnknownProperties(this IDictionary<string, object?> source, Type type)
    {
        var obj = Activator.CreateInstance(type);

        Dictionary<string, object?>? unknownPropertyDictionary = null;

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
                        unknownPropertyDictionary = new Dictionary<string, object?>();
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
            return ToObjectWithWithUnknownProperties(objects, fieldType);
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

    internal static void AddAttribute<TSourceType>(ComplexGraphType<TSourceType> complexGraphType, IGraphTypesCache graphTypesCache,
        CkTypeAttributeGraph typeAttributeGraph, bool isInputType) where TSourceType : GraphQlDto
    {
        var attributeName = typeAttributeGraph.AttributeName;
        IGraphType? graphType;
        FieldBuilder<TSourceType, object>? builder;

        switch (typeAttributeGraph.ValueType)
        {
#pragma warning disable GQL005

            case AttributeValueTypesDto.String:
                builder = complexGraphType.Field(attributeName, typeof(StringGraphType));
                break;
            case AttributeValueTypesDto.StringArray:
                builder = complexGraphType.Field(attributeName, typeof(ListGraphType<StringGraphType>));
                break;
            case AttributeValueTypesDto.Int:
                builder = complexGraphType.Field(attributeName, typeof(IntGraphType));
                break;
            case AttributeValueTypesDto.IntArray:
                builder = complexGraphType.Field(attributeName, typeof(ListGraphType<IntGraphType>));
                break;
            case AttributeValueTypesDto.Boolean:
                builder = complexGraphType.Field(attributeName, typeof(BooleanGraphType));
                break;
            case AttributeValueTypesDto.Double:
                builder = complexGraphType.Field(attributeName, typeof(DecimalGraphType));
                break;
            case AttributeValueTypesDto.DateTime:
                builder = complexGraphType.Field(attributeName, typeof(DateTimeGraphType));
                break;
            case AttributeValueTypesDto.DateTimeOffset:
                builder = complexGraphType.Field(attributeName, typeof(DateTimeOffsetGraphType));
                break;
            case AttributeValueTypesDto.TimeSpan:
                builder = complexGraphType.Field(attributeName, typeof(TimeSpanSecondsGraphType));
                break;
            case AttributeValueTypesDto.Int64:
                builder = complexGraphType.Field(attributeName, typeof(LongGraphType));
                break;
            case AttributeValueTypesDto.BinaryLinked:
                builder = complexGraphType.Field(attributeName, typeof(OctoObjectIdType));
                break;
            case AttributeValueTypesDto.Enum:
                if (typeAttributeGraph.ValueCkEnumId == null)
                {
                    throw OctoGraphQLException.EnumAttributeHasNoCkEnumId(typeAttributeGraph.AttributeName);
                }

                builder = complexGraphType.Field(attributeName,
                    graphTypesCache.GetEnum(typeAttributeGraph.ValueCkEnumId));
                break;
            case AttributeValueTypesDto.Record:
                if (typeAttributeGraph.ValueCkRecordId == null)
                {
                    throw OctoGraphQLException.RecordAttributeHasNoCkRecordId(typeAttributeGraph.AttributeName);
                }

                graphType = isInputType switch
                {
                    true => graphTypesCache.GetRecordInput(typeAttributeGraph.ValueCkRecordId),
                    _ => graphTypesCache.GetRecord(typeAttributeGraph.ValueCkRecordId)
                };

                builder = complexGraphType.Field(attributeName, graphType);
                break;
            case AttributeValueTypesDto.RecordArray:
                if (typeAttributeGraph.ValueCkRecordId == null)
                {
                    throw OctoGraphQLException.RecordAttributeHasNoCkRecordId(typeAttributeGraph.AttributeName);
                }

                graphType = isInputType switch
                {
                    true => graphTypesCache.GetRecordInput(typeAttributeGraph.ValueCkRecordId),
                    _ => graphTypesCache.GetRecord(typeAttributeGraph.ValueCkRecordId)
                };

                builder = complexGraphType.Field(attributeName, new ListGraphType(graphType));
                break;
            default:
                throw OctoGraphQLException.AttributeValueTypeNotSupported(typeAttributeGraph.ValueType);
#pragma warning restore GQL005
        }

        builder = builder.Metadata(Statics.AttributeGraphType, typeAttributeGraph);
        if (!isInputType)
        {
            builder.Resolve(ResolveAttributeValue);
        }
    }

    private static object? ResolveAttributeValue<TSourceType>(IResolveFieldContext<TSourceType> context) where TSourceType : GraphQlDto
    {
        var rtTypeWithAttributes = context.Source.UserContext as RtTypeWithAttributes;
        var typeAttributeGraph = context.FieldDefinition.GetMetadata<CkTypeAttributeGraph>(Statics.AttributeGraphType);

        var r = rtTypeWithAttributes?.GetAttributeValueOrDefault(typeAttributeGraph.AttributeName);
        switch (typeAttributeGraph.ValueType)
        {
            case AttributeValueTypesDto.Record:
                if (r is RtRecord rtRecord)
                {
                    return RtRecordDtoType.CreateRtRecordDto(rtRecord);
                }

                break;
            case AttributeValueTypesDto.RecordArray:
                if (r is IEnumerable items)
                {
                    return items.Cast<RtRecord>().Select(RtRecordDtoType.CreateRtRecordDto).ToList();
                }

                break;
            case AttributeValueTypesDto.TimeSpan:
                if (r is string timeSpanString)
                {
                    return TimeSpan.Parse(timeSpanString);
                }

                break;
        }

        return r;
    }
}