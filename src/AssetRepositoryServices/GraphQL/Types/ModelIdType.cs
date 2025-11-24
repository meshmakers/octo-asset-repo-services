using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal sealed class ModelIdType : ObjectGraphType<CkModelId>
{
    public ModelIdType()
    {
        Name = nameof(CkModelId);
        Description = "Identifies a construction kit model.";

        Field(x => x.SemanticVersionedFullName)
            .Description("The semantic versioned full name of the model, e.g. 'System' or 'System-2'.");
        Field(x => x.Name)
            .Description("The name of the model, e.g. 'System'.");
        Field(x => x.Version, typeof(NonNullGraphType<CkVersionType>))
            .Description("The version of the model, e.g. '1.0.0' or '2.0.0'.");
        Field(x => x.FullName)
            .Description("The full name of the model, e.g. 'System-1.0.3'.");
    }
}