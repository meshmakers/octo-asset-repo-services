using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     Implements the Octo schema for a given data source
/// </summary>
internal sealed class OctoSchema : Schema
{
    static OctoSchema()
    {
        ValueConverter.Register(typeof(string), typeof(OctoObjectId), o => new OctoObjectId((string)o));
    }

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="octoQuery">The Octo query schema of a given data source</param>
    /// <param name="octoMutation"></param>
    /// <param name="octoSubscriptions"></param>
    public OctoSchema(OctoQuery octoQuery, OctoMutation octoMutation, OctoSubscriptions octoSubscriptions)
    {
        Query = octoQuery;
        Mutation = octoMutation;
        Subscription = octoSubscriptions;
    }
}