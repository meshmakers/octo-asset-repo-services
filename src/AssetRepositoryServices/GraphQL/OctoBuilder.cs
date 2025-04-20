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
using Meshmakers.Octo.ConstructionKit.Contracts;
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

    internal OctoBuilder<TSourceType> Attribute(IGraphTypesCache graphTypesCache, CkTypeAttributeGraph typeAttributeGraph, bool isInputType)
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
        if (!isInputType)
        {
            builder.ResolveAsync(ResolveAttributeValueAsync);
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
            case AttributeValueTypesDto.Enum:
                if (typeAttributeGraph.ValueCkEnumId == null)
                {
                    throw OctoGraphQLException.EnumAttributeHasNoCkEnumId(typeAttributeGraph.AttributeName);
                }

                return (graphTypesCache.GetEnum(typeAttributeGraph.ValueCkEnumId), null);
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

                return (graphType, null);
            case AttributeValueTypesDto.RecordArray:
                if (typeAttributeGraph.ValueCkRecordId == null)
                {
                    throw OctoGraphQLException.RecordAttributeHasNoCkRecordId(typeAttributeGraph.AttributeName);
                }

                graphType = isInputType switch
                {
                    true => graphTypesCache.GetRecordInput(typeAttributeGraph.ValueCkRecordId),
                    _ => new NonNullGraphType(graphTypesCache.GetRecord(typeAttributeGraph.ValueCkRecordId))
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

    private async Task<object?> ResolveAttributeValueAsync(IResolveFieldContext<TSourceType> context)
    {
        var tenantContext = Helpers.GetTenantContext(context.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();
        var rtTypeWithAttributes = context.Source.UserContext as RtTypeWithAttributes;
        var typeAttributeGraph = context.FieldDefinition.GetMetadata<CkTypeAttributeGraph>(Statics.AttributeGraphType);

        var r = rtTypeWithAttributes?.GetAttributeValueOrDefault(typeAttributeGraph.AttributeName);
        switch (typeAttributeGraph.ValueType)
        {
            case AttributeValueTypesDto.BinaryLinked:
                if (r is OctoObjectId binaryId)
                {
                    var downloadInfo = await tenantRepository.GetLargeBinaryAsync(binaryId);
                    if (downloadInfo == null)
                    {
                        throw OctoGraphQLException.LargeBinaryNotFound(binaryId);
                    }

                    return new LargeBinaryInfoDto
                    {
                        ContentType = downloadInfo.ContentType,
                        BinaryId = downloadInfo.BinaryId,
                        Filename = downloadInfo.Filename,
                        Length = downloadInfo.Length,
                        UploadDateTime = downloadInfo.UploadDateTime,
                        DownloadUri = new Uri(options.Value.PublicUrl.EnsureEndsWith($"/{tenantContext.TenantId}/v1/largeBinaries?largeBinaryId={downloadInfo.BinaryId}"))
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
        }

        return r;
    }
}