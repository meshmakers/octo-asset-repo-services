using GraphQL.Types.Relay.DataObjects;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <inheritdoc />
internal class OctoConnection<TNode, TEdge> : Connection<TNode, TEdge> where TEdge : Edge<TNode>
{
    /// <summary>
    ///     The result for aggregations of the items in this connection.
    /// </summary>
    public AggregationResult? Aggregation { get; set; }

    /// <summary>
    ///     The result for field aggregations of the items in this connection.
    /// </summary>
    public IEnumerable<FieldAggregationResult>? FieldAggregations { get; set; }
}

/// <inheritdoc />
internal class OctoConnection<TNode> : OctoConnection<TNode, Edge<TNode>>;