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
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
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

    internal OctoBuilder<TSourceType> Attribute(IGraphTypesCache graphTypesCache,
        CkTypeAttributeGraph typeAttributeGraph, bool isInputType, bool isInterface = false)
    {
        // CK v2 (AB#5668): the single chokepoint for attribute fields of entity, interface, input, update and
        // record types. Hidden attributes never appear in GraphQL; MethodOnly attributes are readable but not part
        // of the generic input types.
        if (!AttributeAccess.IsExposedInOutput(typeAttributeGraph.Access) ||
            (isInputType && !AttributeAccess.IsExposedInGenericInput(typeAttributeGraph.Access)))
        {
            return this;
        }

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
        return OctoValueTypeMapper.GetValueFieldType(graphTypesCache, typeAttributeGraph.ValueType, typeAttributeGraph.ValueCkEnumId,
            typeAttributeGraph.ValueCkRecordId, typeAttributeGraph.AttributeName, isInputType);
    }

    private object? ResolveAttributeValue(IResolveFieldContext<TSourceType> context)
    {
        var tenantContext = Helpers.GetTenantContext(context.UserContext);
        var rtTypeWithAttributes = context.Source.UserContext as RtTypeWithAttributes;
        var typeAttributeGraph = context.FieldDefinition.GetMetadata<CkTypeAttributeGraph>(Statics.AttributeGraphType);

        var r = rtTypeWithAttributes?.GetAttributeValueOrDefault(typeAttributeGraph.AttributeName);

        // AB#5528: decided by the CK attribute type, never by the runtime value - change-stream documents
        // carry legacy plain strings that are not normalised to RtSecretValue. The value itself (plaintext,
        // legacy clear text or envelope) never leaves this resolver.
        if (typeAttributeGraph.ValueType == AttributeValueTypesDto.Secret)
        {
            return SecretAttributeProjection.ToSecretState(r, context.GetProtector());
        }

        if (r is RtSecretValue)
        {
            // A secret in a slot the (stale) CK cache does not know as Secret: the field is not typed
            // OctoSecretState, so nothing can be said about it without leaking - project "no value".
            return null;
        }

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
                // A TimeSpan attribute value can arrive as a real TimeSpan, an Int64/Int32 tick count,
                // or a string — either a bare-integer tick count (the shape ImportRt's export/import
                // JSON round-trip produces, AB#4259) or a .NET / ISO-8601 literal. A bare-integer string
                // is ticks, NOT a .NET literal: TimeSpan.Parse("9000000000") reads it as 9-billion days
                // and throws OverflowException. Mirror RtTypeWithAttributes.TryCoerceTimeSpan here.
                switch (r)
                {
                    case TimeSpan ts:
                        return ts;
                    case long ticks:
                        return TimeSpan.FromTicks(ticks);
                    case int ticks32:
                        return TimeSpan.FromTicks(ticks32);
                    case string s when long.TryParse(s, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var tickString):
                        return TimeSpan.FromTicks(tickString);
                    case string s when TimeSpan.TryParse(s,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed):
                        return parsed;
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