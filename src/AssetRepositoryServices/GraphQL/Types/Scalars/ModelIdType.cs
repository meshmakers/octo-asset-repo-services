using GraphQL.Types;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;

internal class ModelIdType : ScalarGraphType
{
    public ModelIdType()
    {
        Name = nameof(CkModelId);
        Description = "Identifies a construction kit model.";
    }

    public override object? ParseValue(object? value)
    {
        return value switch
        {
            string s => new CkModelId(s),
            CkModelId _ => value,
            _ => ThrowValueConversionError(value)
        };
    }

    public override object? Serialize(object? value)
    {
        if (value is CkModelId ckModelId)
        {
            return ckModelId.SemanticVersionedFullName;
        }

        return null;
    }
}