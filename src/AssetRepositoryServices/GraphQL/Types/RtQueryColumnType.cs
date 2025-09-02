using AssetRepositoryServices.Resources;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements the GraphQL type for <see cref="RtQueryColumnDto" />.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RtQueryColumnType : ObjectGraphType<RtQueryColumnDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public RtQueryColumnType()
    {
        Name = "RtQueryColumn";
        Description = AssetTexts.Graphql_RtQueryColumn_Description;
        Field(d => d.AttributePath, typeof(StringGraphType));
        Field(d => d.AttributeValueType, typeof(AttributeValueTypesDtoType));
    }

    public static RtQueryColumnDto CreateRtQueryColumnDto(CkTypeQueryColumn ckTypeQueryColumn)
    {
        var rtQueryColumnDto = new RtQueryColumnDto
        {
            AttributePath = ckTypeQueryColumn.Path,
            AttributeValueType = ckTypeQueryColumn.ValueType,
            UserContext = ckTypeQueryColumn
        };
        return rtQueryColumnDto;
    }
}