using GraphQL;
using GraphQL.Types;
using GraphQL.Types.Relay;
using Meshmakers.Common.Shared;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements a Connection GraphQL type for dynamic creation (without using generic types - because we create types
///     based on data source settings!)
/// </summary>
[DoNotRegister]
internal sealed class DynamicConnectionType : ObjectGraphType
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="name">The name of the connection type</param>
    /// <param name="description">Description of the connection type</param>
    /// <param name="itemType">The used item type</param>
    /// <param name="edgeType">The used edge type</param>
    public DynamicConnectionType(string name, string description, IGraphType itemType, IGraphType edgeType)
    {
        ArgumentValidation.ValidateString(nameof(name), name);
        ArgumentValidation.ValidateString(nameof(description), description);

        Name = name;
        Description = description;
        Field<IntGraphType>("totalCount")
            .Description(
                "A count of the total number of objects in this connection, ignoring pagination. This allows a client to fetch the first five objects by passing \"5\" as the argument to `first`, then fetch the total count so it could display \"5 of 83\", for example. In cases where we employ infinite scrolling or don't have an exact count of entries, this field will return `null`.");
        Field<PageInfoType>("pageInfo").Description("Information to aid in pagination.");

        var listType = new ListGraphType(itemType);
        this.Field("items",
            "A list of all of the objects returned in the connection. This is a convenience field provided for quickly exploring the API; rather than querying for \"{ edges { node } }\" when no edge data is needed, this field can be used instead. Note that when clients like Relay need to fetch the \"cursor\" field on the edge to enable efficient pagination, this shortcut cannot be used, and the full \"{ edges { node } } \" version should be used instead.",
            listType);

        Field<AggregationResultType>("aggregation").Description("Result of aggregating the items of the result set.");
        Field<ListGraphType<FieldAggregationResultType>>("fieldAggregations").Description("Result of aggregating the items by fields.");

        var edgeListType = new ListGraphType(edgeType);
        this.Field("edges", "Information to aid in pagination.", edgeListType);
    }
}