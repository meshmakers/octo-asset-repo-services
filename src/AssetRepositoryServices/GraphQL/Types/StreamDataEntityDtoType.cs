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
using Meshmakers.Octo.Services.Common.StreamData.Dtos;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements the GraphQL Stream data Entity Type
/// </summary>
[DoNotRegister]
internal sealed class StreamDataEntityDtoType : ObjectGraphType<StreamDataEntityDto>
{
    private readonly CkTypeGraph _ckTypeGraph;

    public string ConnectionName { get; }

    /// <inheritdoc />
    public StreamDataEntityDtoType(CkTypeGraph ckTypeGraph)
    {
        _ckTypeGraph = ckTypeGraph;

        //name results to the connection type.
        // e.g. StreamDataIndustryEnergyMeterConnection
        Name = _ckTypeGraph.CkTypeId.GetGraphQlPascalCaseNameForStreamData();

        // this results to the connection Name
        // data can be queried like this
        // query{
        //  streamData{
        //      industryEnergyMeter{
        //          items{
        //              rtId
        //              name
        //          }
        //      }
        //   }
        // }
        // 

        ConnectionName = _ckTypeGraph.CkTypeId.GetGraphQlPascalCaseName();

        Description = $"Stream data entities of construction kit type '{_ckTypeGraph.CkTypeId}'";
        IsTypeOf = o =>
        {
            if (o is StreamDataEntityDto rtEntityDto)
            {
                return _ckTypeGraph.GetAllDerivedTypes(true).Contains(rtEntityDto.CkTypeId);
            }

            return false;
        };

        Field(d => d.RtId, type: typeof(NonNullGraphType<OctoObjectIdType>));
        Field(d => d.CkTypeId, type: typeof(NonNullGraphType<CkIdTypeGraph<CkTypeId>>));
        Field(d => d.TimeStamp, type: typeof(DateTimeGraphType));
        Field(d => d.RtWellKnownName, type: typeof(StringGraphType));
        Field(d => d.RtCreationDateTime, type: typeof(DateTimeGraphType));
        Field(d => d.RtChangedDateTime, type: typeof(DateTimeGraphType))
            .Argument<AttributeTsArgumentGraphType>(Statics.StreamDataAttributeArgument,
            "Arguments for stream data.");
    }


    public CkId<CkTypeId> CkTypeId => _ckTypeGraph.CkTypeId;


    internal void Populate(IGraphTypesCache graphTypesCache)
    {
        AddConstructionKit();

        foreach (var attribute in _ckTypeGraph.AllAttributes.Values.Where(x => x.IsDataStream))
        {
            AddStreamDataAttribute(graphTypesCache, attribute);
        }
    }

    private void AddStreamDataAttribute(
        IGraphTypesCache graphTypesCache,
        CkTypeAttributeGraph typeAttributeGraph)
    {
        var attributeName = typeAttributeGraph.AttributeName;
        IGraphType? graphType;
        FieldBuilder<StreamDataEntityDto, object>? builder;

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
        builder.Argument<AttributeTsArgumentGraphType>(Statics.StreamDataAttributeArgument,
            "Arguments for stream data.");
        builder.Resolve(ResolveAttributeValue);
    }

    private static object? ResolveAttributeValue<TSourceType>(IResolveFieldContext<TSourceType> context)
        where TSourceType : StreamDataEntityDto
    {
        var rtTypeWithAttributes = context.Source.UserContext as RtTypeWithAttributes;
        var typeAttributeGraph = context.FieldDefinition.GetMetadata<CkTypeAttributeGraph>(Statics.AttributeGraphType);

        var attributeName = typeAttributeGraph.AttributeName;

        if (context.TryGetArgument(Statics.StreamDataAttributeArgument, out AttributeTsArgumentDto? argument)
            && argument.AggregationType is not null)
        {
            //When we queried an attribute with an aggregation, we do as if it is a normal attribute
            attributeName = $"{argument.AggregationType.ToString()}_{attributeName}";
        }

        var r = rtTypeWithAttributes?.GetAttributeValueOrDefault(attributeName);
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

    private object ResolveCkEntity(IResolveFieldContext<StreamDataEntityDto> arg)
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

    internal static StreamDataEntityDto CreateTsEntityDto(DataPointDto datapoint)
    {
        var tsEntityDto = new StreamDataEntityDto()
        {
            RtId = datapoint.RtId ?? throw OctoGraphQLException.CkTypeIdUndefined(),
            CkTypeId = datapoint.CkTypeId ?? throw OctoGraphQLException.CkTypeIdUndefined(),
            TimeStamp = datapoint.Timestamp,
            RtWellKnownName = datapoint.RtWellKnownName,
            RtCreationDateTime = datapoint.RtCreationDateTime,
            RtChangedDateTime = datapoint.RtChangedDateTime,
            UserContext = datapoint
        };
        return tsEntityDto;
    }
}