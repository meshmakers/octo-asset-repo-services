using GraphQL.Types;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;

internal class GraphDirectionsDtoType : EnumerationGraphType<GraphDirections>
{
    public GraphDirectionsDtoType()
    {
        Name = "GraphDirection";
        Description = "Enum of graph directions";
    }
}