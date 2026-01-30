using System.Collections;
using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts.Geospatial.Geometry;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

internal class OctoBuilder<TSourceType>(
    ComplexGraphType<TSourceType> complexGraphType,
    IOptions<OctoAssetRepositoryServicesOptions> options) where TSourceType : GraphQlDto
{
    internal static OctoBuilder<TSourceType> Create(ComplexGraphType<TSourceType> complexGraphType,
        IOptions<OctoAssetRepositoryServicesOptions> options)
    {
        return new OctoBuilder<TSourceType>(complexGraphType, options);
    }

    internal OctoBuilder<TSourceType> Attribute(IGraphTypesCache graphTypesCache,
        CkTypeAttributeGraph typeAttributeGraph, bool isInputType, bool isInterface = false)
    {
        var attributeName = typeAttributeGraph.AttributeName;

        FieldBuilder<TSourceType, object>? builder;
        var (graphType, type) = GetAttributeFieldType(graphTypesCache, typeAttributeGraph, isInputType);

        if (graphType != null)
        {
            builder = complexGraphType.Field(attributeName,
                !typeAttributeGraph.IsOptional && !isInputType ? new NonNullGraphType(graphType) : graphType);
        }
        else if (type != null)
        {
            builder = complexGraphType.Field(attributeName,
                !typeAttributeGraph.IsOptional && !isInputType
                    ? typeof(NonNullGraphType<>).MakeGenericType(type)
                    : type);
        }
        else
        {
            throw new InvalidOperationException("GraphType and Type cannot be null at the same time.");
        }

        builder = builder.Metadata(Statics.AttributeGraphType, typeAttributeGraph);
        // Interface fields cannot have resolvers - only object types can
        if (!isInputType && !isInterface)
        {
            builder.Resolve(ResolveAttributeValue);
        }

        return this;
    }

    private static (IGraphType?, Type?) GetAttributeFieldType(
        IGraphTypesCache graphTypesCache, CkTypeAttributeGraph typeAttributeGraph, bool isInputType)
    {
        IGraphType? graphType;
        switch (typeAttributeGraph.ValueType)
        {
            case AttributeValueTypesDto.String:
                return (null, typeof(StringGraphType));
            case AttributeValueTypesDto.StringArray:
                return (null, typeof(ListGraphType<NonNullGraphType<StringGraphType>>));
            case AttributeValueTypesDto.Int:
                return (null, typeof(IntGraphType));
            case AttributeValueTypesDto.IntArray:
                return (null, typeof(ListGraphType<NonNullGraphType<IntGraphType>>));
            case AttributeValueTypesDto.Boolean:
                return (null, typeof(BooleanGraphType));
            case AttributeValueTypesDto.Double:
                return (null, typeof(DecimalGraphType));
            case AttributeValueTypesDto.DateTime:
                return (null, typeof(DateTimeGraphType));
            case AttributeValueTypesDto.DateTimeOffset:
                return (null, typeof(DateTimeOffsetGraphType));
            case AttributeValueTypesDto.TimeSpan:
                return (null, typeof(TimeSpanSecondsGraphType));
            case AttributeValueTypesDto.Int64:
                return (null, typeof(LongGraphType));
            case AttributeValueTypesDto.BinaryLinked:
                var binaryLinkedType = isInputType switch
                {
                    true => typeof(LargeBinaryDtoType),
                    _ => typeof(LargeBinaryInfoDtoType)
                };
                return (null, binaryLinkedType);
            case AttributeValueTypesDto.Binary:
                return (null, typeof(ListGraphType<ByteGraphType>));

            case AttributeValueTypesDto.Enum:
                if (typeAttributeGraph.ValueCkEnumId == null)
                {
                    throw OctoGraphQLException.EnumAttributeHasNoCkEnumId(typeAttributeGraph.AttributeName);
                }

                return (graphTypesCache.GetEnum(typeAttributeGraph.ValueCkEnumId.ToRtCkId()), null);
            case AttributeValueTypesDto.Record:
                if (typeAttributeGraph.ValueCkRecordId == null)
                {
                    throw OctoGraphQLException.RecordAttributeHasNoCkRecordId(typeAttributeGraph.AttributeName);
                }

                graphType = isInputType switch
                {
                    true => graphTypesCache.GetRecordInput(typeAttributeGraph.ValueCkRecordId.ToRtCkId()),
                    _ => graphTypesCache.GetRecord(typeAttributeGraph.ValueCkRecordId.ToRtCkId())
                };

                return (graphType, null);
            case AttributeValueTypesDto.RecordArray:
                if (typeAttributeGraph.ValueCkRecordId == null)
                {
                    throw OctoGraphQLException.RecordAttributeHasNoCkRecordId(typeAttributeGraph.AttributeName);
                }

                graphType = isInputType switch
                {
                    true => graphTypesCache.GetRecordInput(typeAttributeGraph.ValueCkRecordId.ToRtCkId()),
                    _ => new NonNullGraphType(graphTypesCache.GetRecord(typeAttributeGraph.ValueCkRecordId.ToRtCkId()))
                };

                return (new ListGraphType(graphType), null);
            case AttributeValueTypesDto.GeospatialPoint:

                var pointType = isInputType switch
                {
                    true => typeof(PointInputGraphType),
                    _ => typeof(RtGeospatialValueDtoType)
                };
                return (null, pointType);
            default:
                throw OctoGraphQLException.AttributeValueTypeNotSupported(typeAttributeGraph.ValueType);
        }
    }

    private object? ResolveAttributeValue(IResolveFieldContext<TSourceType> context)
    {
        var tenantContext = Helpers.GetTenantContext(context.UserContext);
        var rtTypeWithAttributes = context.Source.UserContext as RtTypeWithAttributes;
        var typeAttributeGraph = context.FieldDefinition.GetMetadata<CkTypeAttributeGraph>(Statics.AttributeGraphType);

        var r = rtTypeWithAttributes?.GetAttributeValueOrDefault(typeAttributeGraph.AttributeName);
        switch (typeAttributeGraph.ValueType)
        {
            case AttributeValueTypesDto.BinaryLinked:
                if (r is EntityBinaryInfo entityBinaryInfo)
                {
                    return new LargeBinaryInfoDto
                    {
                        ContentType = entityBinaryInfo.ContentType,
                        BinaryId = entityBinaryInfo.BinaryId,
                        Filename = entityBinaryInfo.Filename,
                        Size = entityBinaryInfo.Size,
                        DownloadUri = new Uri(options.Value.PublicUrl.EnsureEndsWith(
                            $"/{tenantContext.TenantId}/v1/largeBinaries?largeBinaryId={entityBinaryInfo.BinaryId}"))
                    };
                }

                return null;
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
            case AttributeValueTypesDto.GeospatialPoint:
                if (r is Point point)
                {
                    return new RtGeospatialValueDto
                    {
                        Distance = rtTypeWithAttributes?.GetAttributeValueOrDefault(
                            typeAttributeGraph.AttributeName + "_distance", default(double?)),
                        Point = point
                    };
                }

                break;
            case AttributeValueTypesDto.Binary:
                // Handle Binary data - convert from various formats to byte[]
                if (r == null)
                {
                    return null;
                }

                if (r is byte[] bytes)
                {
                    return bytes;
                }

                // Handle List<object> from legacy storage or MongoDB deserialization issues
                if (r is IEnumerable<object> objectList)
                {
                    return objectList.Select(item => Convert.ToByte(item)).ToArray();
                }

                // Handle generic IEnumerable
                if (r is IEnumerable enumerable and not string)
                {
                    var byteList = new List<byte>();
                    foreach (var item in enumerable)
                    {
                        byteList.Add(Convert.ToByte(item));
                    }
                    return byteList.ToArray();
                }

                throw new InvalidOperationException(
                    $"Unable to convert Binary attribute value of type '{r.GetType().FullName}' to byte[].");
        }

        // If value is null and attribute has default values, use the first default value.
        // This handles the case where legacy data was created before an attribute with default value
        // was added to the schema (bug AB#3307).
        if (r == null && typeAttributeGraph.DefaultValues is { Count: > 0 })
        {
            return typeAttributeGraph.DefaultValues.First();
        }

        return r;
    }
}