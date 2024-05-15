using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     Implements the Octo schema for a given data source
/// </summary>
[DoNotRegister]
internal sealed class OctoSchema : Schema
{
    static OctoSchema()
    {
        ValueConverter.Register(typeof(string), typeof(OctoObjectId), o => new OctoObjectId((string)o));
    }

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="serviceProvider">The DI based service provider</param>
    /// <param name="octoQuery">The Octo query schema of a given data source</param>
    /// <param name="octoMutation"></param>
    /// <param name="octoSubscriptions"></param>
    public OctoSchema(IServiceProvider serviceProvider, OctoQuery octoQuery, OctoMutation octoMutation, OctoSubscriptions octoSubscriptions)
        :base(serviceProvider)
    {
        Query = octoQuery;
        Mutation = octoMutation;
        Subscription = octoSubscriptions;
    }
}