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

/// <summary>
///     The single CK value type → GraphQL type mapping.
/// </summary>
internal static class OctoValueTypeMapper
{
    /// <summary>
    ///     Maps a CK value type to the GraphQL type used for it. Shared by attribute fields and CK v2 method
    ///     parameters / results (AB#5670), so both surfaces always agree on the scalar mapping.
    /// </summary>
    /// <returns>Either a graph type instance or a graph type CLR type</returns>
    internal static (IGraphType?, Type?) GetValueFieldType(IGraphTypesCache graphTypesCache,
        AttributeValueTypesDto valueType, CkId<CkEnumId>? valueCkEnumId, CkId<CkRecordId>? valueCkRecordId,
        string name, bool isInputType)
    {
        IGraphType? graphType;
        switch (valueType)
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
                return (null, typeof(UtcDateTimeGraphType));
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
            case AttributeValueTypesDto.Secret:
                // AB#5528 (concept §4.1/§4.3): a secret is written as a plain string (omitted, null or ""
                // = unchanged; clearing only via clearSecretAttributes) and read as { isSet } only.
                return (null, isInputType ? typeof(StringGraphType) : typeof(OctoSecretStateDtoType));

            case AttributeValueTypesDto.Enum:
                if (valueCkEnumId == null)
                {
                    throw OctoGraphQLException.EnumAttributeHasNoCkEnumId(name);
                }

                return (graphTypesCache.GetEnum(valueCkEnumId.ToRtCkId()), null);
            case AttributeValueTypesDto.Record:
                if (valueCkRecordId == null)
                {
                    throw OctoGraphQLException.RecordAttributeHasNoCkRecordId(name);
                }

                graphType = isInputType switch
                {
                    true => graphTypesCache.GetRecordInput(valueCkRecordId.ToRtCkId()),
                    _ => graphTypesCache.GetRecord(valueCkRecordId.ToRtCkId())
                };

                return (graphType, null);
            case AttributeValueTypesDto.RecordArray:
                if (valueCkRecordId == null)
                {
                    throw OctoGraphQLException.RecordAttributeHasNoCkRecordId(name);
                }

                graphType = isInputType switch
                {
                    true => graphTypesCache.GetRecordInput(valueCkRecordId.ToRtCkId()),
                    _ => new NonNullGraphType(graphTypesCache.GetRecord(valueCkRecordId.ToRtCkId()))
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
                throw OctoGraphQLException.AttributeValueTypeNotSupported(valueType);
        }
    }
}
