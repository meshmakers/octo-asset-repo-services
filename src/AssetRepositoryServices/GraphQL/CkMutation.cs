using AssetRepositoryServices.Resources;
using GraphQL.Types;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class CkMutation : ObjectGraphType
{
    public CkMutation()
    {
        Name = "ConstructionKitMutations";
        Description = AssetTexts.Graphql_CkMutation_Description;

        Field<CkEnumMutation>("Enums")
            .Argument<StringGraphType>(Statics.CkIdArg, AssetTexts.Graphql_CkMutation_CkId_Description)
            .Argument<ListGraphType<StringGraphType>>(Statics.CkIdsArg,
                AssetTexts.Graphql_CkMutation_CkIds_Description)
            .Resolve(_ => new object());
    }
}