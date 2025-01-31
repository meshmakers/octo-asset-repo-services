using AssetRepositoryServices.Resources;
using GraphQL.Types;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements the GraphQL type for <see cref="RtQueryColumnDto"/>.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RtQueryColumnType: ObjectGraphType<RtQueryColumnDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public RtQueryColumnType()
    {
        Name = "RtQueryColumn";
        Description = AssetTexts.Graphql_RtQueryRow_Description;
        Field(d => d.AttributePath, type: typeof(StringGraphType));
        Field(d => d.AttributeValueType, type: typeof(AttributeValueTypesDtoType));
    }
    
    public static RtQueryColumnDto CreateRtQueryColumnDto(CkTypeGraph ckTypeGraph, ConstructionKit.Models.System.Generated.System.v1.RtQuery rtQuery, string attributePath)
    {
        var pascalCaseAttributePath = attributePath.ToPascalCase();
        if (ckTypeGraph.AllAttributesByName.ContainsKey(pascalCaseAttributePath))
        {
            var rtQueryColumnDto = new RtQueryColumnDto
            {
                AttributePath = attributePath,
                AttributeValueType = ckTypeGraph.AllAttributesByName[attributePath.ToPascalCase()].ValueType,
                UserContext = rtQuery
            };
            return rtQueryColumnDto;
        }

        if (pascalCaseAttributePath == nameof(RtEntityDto.RtId))
        {
            return new RtQueryColumnDto
            {
                AttributePath = attributePath,
                AttributeValueType = AttributeValueTypesDto.String,
                UserContext = rtQuery
            };
        }

        if (pascalCaseAttributePath == nameof(RtEntityDto.RtWellKnownName))
        {
            return new RtQueryColumnDto
            {
                AttributePath = attributePath,
                AttributeValueType = AttributeValueTypesDto.String,
                UserContext = rtQuery
            };
        }

        if (pascalCaseAttributePath == nameof(RtEntityDto.RtVersion))
        {
            return new RtQueryColumnDto
            {
                AttributePath = attributePath,
                AttributeValueType = AttributeValueTypesDto.Int64,
                UserContext = rtQuery
            };
        }

        if (pascalCaseAttributePath == nameof(RtEntityDto.RtCreationDateTime))
        {
            return new RtQueryColumnDto
            {
                AttributePath = attributePath,
                AttributeValueType = AttributeValueTypesDto.DateTime,
                UserContext = rtQuery
            };
        }

        if (pascalCaseAttributePath == nameof(RtEntityDto.RtChangedDateTime))
        {
            return new RtQueryColumnDto
            {
                AttributePath = attributePath,
                AttributeValueType = AttributeValueTypesDto.DateTime,
                UserContext = rtQuery
            };
        }

        throw OctoGraphQLException.InvalidQueryColumn(rtQuery.RtId, attributePath);
    }
}