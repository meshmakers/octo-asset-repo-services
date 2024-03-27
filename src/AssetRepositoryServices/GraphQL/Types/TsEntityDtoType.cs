using System.Collections;
using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Services.Common.Timeseries;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements the GraphQL Time series Entity Type
/// </summary>
internal sealed class TsEntityDtoType : ObjectGraphType<TsEntityDto>
{
    private readonly CkTypeGraph _ckTypeGraph;

    /// <inheritdoc />
    public TsEntityDtoType(CkTypeGraph ckTypeGraph)
    {
        _ckTypeGraph = ckTypeGraph;

        Name = _ckTypeGraph.CkTypeId.GetGraphQlPascalCaseNameForTs();
        Description = $"Time series entities of construction kit type '{_ckTypeGraph.CkTypeId}'";
        IsTypeOf = o =>
        {
            if (o is TsEntityDto rtEntityDto)
            {
                return _ckTypeGraph.GetAllDerivedTypes(true).Contains(rtEntityDto.CkTypeId);
            }

            return false;
        };

        Field(d => d.RtId, type: typeof(NonNullGraphType<OctoObjectIdType>));
        Field(d => d.CkTypeId, type: typeof(NonNullGraphType<CkIdTypeGraph<CkTypeId>>));
        Field(d => d.TimeStamp, type: typeof(DateTimeGraphType));
    }


    public CkId<CkTypeId> CkTypeId => _ckTypeGraph.CkTypeId;


    internal void Populate(IGraphTypesCache graphTypesCache)
    {
        AddConstructionKit();

        foreach (var attribute in _ckTypeGraph.AllAttributes.Values.Where(x => x.IsDataStream))
        {
            AddTimeSeriesAttribute(graphTypesCache, attribute);
        }
    }

    private void AddTimeSeriesAttribute(
        IGraphTypesCache graphTypesCache,
        CkTypeAttributeGraph typeAttributeGraph)
    {
        var attributeName = typeAttributeGraph.AttributeName;
        IGraphType? graphType;
        FieldBuilder<TsEntityDto, object>? builder;

        switch (typeAttributeGraph.ValueType)
        {
#pragma warning disable GQL005

            case AttributeValueTypesDto.String:
                builder = Field(attributeName, typeof(StringGraphType));
                break;
            case AttributeValueTypesDto.StringArray:
                builder = Field(attributeName, typeof(ListGraphType<StringGraphType>));
                break;
            case AttributeValueTypesDto.Int:
                builder = Field(attributeName, typeof(IntGraphType));
                break;
            case AttributeValueTypesDto.IntArray:
                builder = Field(attributeName, typeof(ListGraphType<IntGraphType>));
                break;
            case AttributeValueTypesDto.Boolean:
                builder = Field(attributeName, typeof(BooleanGraphType));
                break;
            case AttributeValueTypesDto.Double:
                builder = Field(attributeName, typeof(DecimalGraphType));
                break;
            case AttributeValueTypesDto.DateTime:
                builder = Field(attributeName, typeof(DateTimeGraphType));
                break;
            case AttributeValueTypesDto.DateTimeOffset:
                builder = Field(attributeName, typeof(DateTimeOffsetGraphType));
                break;
            case AttributeValueTypesDto.TimeSpan:
                builder = Field(attributeName, typeof(TimeSpanSecondsGraphType));
                break;
            case AttributeValueTypesDto.Int64:
                builder = Field(attributeName, typeof(LongGraphType));
                break;
            case AttributeValueTypesDto.BinaryLinked:
                builder = Field(attributeName, typeof(OctoObjectIdType));
                break;
            case AttributeValueTypesDto.Enum:
                if (typeAttributeGraph.ValueCkEnumId == null)
                {
                    throw OctoGraphQLException.EnumAttributeHasNoCkEnumId(typeAttributeGraph.AttributeName);
                }

                builder = Field(attributeName, graphTypesCache.GetEnum(typeAttributeGraph.ValueCkEnumId));
                break;
            case AttributeValueTypesDto.Record:
                if (typeAttributeGraph.ValueCkRecordId == null)
                {
                    throw OctoGraphQLException.RecordAttributeHasNoCkRecordId(typeAttributeGraph.AttributeName);
                }
                
                graphType = graphTypesCache.GetRecord(typeAttributeGraph.ValueCkRecordId);
                builder = Field(attributeName, graphType);
                break;
            case AttributeValueTypesDto.RecordArray:
                if (typeAttributeGraph.ValueCkRecordId == null)
                {
                    throw OctoGraphQLException.RecordAttributeHasNoCkRecordId(typeAttributeGraph.AttributeName);
                }

                graphType = graphTypesCache.GetRecord(typeAttributeGraph.ValueCkRecordId);
                builder = Field(attributeName, new ListGraphType(graphType));
                break;
            default:
                throw OctoGraphQLException.AttributeValueTypeNotSupported(typeAttributeGraph.ValueType);
#pragma warning restore GQL005
        }

        builder = builder.Metadata(Statics.AttributeGraphType, typeAttributeGraph);
        builder.Argument<TimeFilterGraphType>(Statics.TimeSeriesFilterArg, "Filter for time series data.");
        builder.Resolve(ResolveAttributeValue);
    }
    
    private static object? ResolveAttributeValue<TSourceType>(IResolveFieldContext<TSourceType> context) where TSourceType : TsEntityDto
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

    private void AddConstructionKit()
    {
        Field<CkTypeDtoType>("ConstructionKitType")
            .Resolve(ResolveCkEntity);
    }

    private object ResolveCkEntity(IResolveFieldContext<TsEntityDto> arg)
    {
        var ckCacheService = arg.RequestServices?.GetRequiredService<ICkCacheService>();
        if (ckCacheService == null)
        {
            throw AssetRepositoryException.ServiceNotRegistered(typeof(ICkCacheService));
        }

        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        var ckTypeGraph = ckCacheService.GetCkType(graphQlUserContext.TenantId, arg.Source.CkTypeId);
        return CkTypeDtoType.CreateCkTypeDto(ckTypeGraph);
    }

    internal static TsEntityDto CreateTsEntityDto(DataPointDto tsEntity)
    {
        var rtEntityDto = new TsEntityDto()
        {
            RtId = tsEntity.RtId ?? throw OctoGraphQLException.CkTypeIdUndefined(),
            CkTypeId = tsEntity.CkTypeId ?? throw OctoGraphQLException.CkTypeIdUndefined(),
            TimeStamp = tsEntity.Timestamp,
            UserContext = tsEntity
        };
        return rtEntityDto;
    }
}