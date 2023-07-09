using System.Collections.Generic;
using GraphQL.Types.Relay.DataObjects;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <inheritdoc />
public class OctoConnection<TNode, TEdge> : Connection<TNode, TEdge> where TEdge : Edge<TNode>
{
    /// <summary>
    /// The result when a grouping is requested.
    /// </summary>
    public IEnumerable<GroupingDto>? Groupings { get; set; } 
}   

/// <inheritdoc />
public class OctoConnection<TNode> : OctoConnection<TNode, Edge<TNode>>
{
}