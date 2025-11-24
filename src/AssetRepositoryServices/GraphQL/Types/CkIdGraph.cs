using GraphQL.Types;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class CkIdGraph<TCkKey> : ObjectGraphType<CkId<TCkKey>> where TCkKey : IComparable<TCkKey>, ICkElementId
{
    public CkIdGraph()
    {
        Name = typeof(TCkKey).Name;
        Description = "A construction kit id of " + typeof(TCkKey).Name + ".";

        Field(x => x.SemanticVersionedFullName)
            .Description("The semantic versioned full name of the construction kit type, e.g. 'System/Entity' or 'System-2/Entity'.");
        // Field(x => x.ElementId)
        //     .Description("The name of the type, e.g. 'Entity'.");
        // Field(x => x.ElementId., typeof(NonNullGraphType<CkVersionType>))
        //     .Description("The version of the model, e.g. '1.0.0' or '2.0.0'.");
        Field(x => x.FullName)
            .Description("The full name of the model, e.g. 'System-1.0.3'.");
    }
}