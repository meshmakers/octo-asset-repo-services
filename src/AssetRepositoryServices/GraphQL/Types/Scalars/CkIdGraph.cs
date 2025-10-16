using GraphQL.Types;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;

internal class CkIdGraph<TCkKey> : ScalarGraphType where TCkKey : IComparable<TCkKey>, ICkElementId
{
    public CkIdGraph()
    {
        Name = typeof(TCkKey).Name;
        Description = "A construction kit id of " + typeof(TCkKey).Name + ".";
    }

    public override object ParseValue(object? value)
    {
        return value switch
        {
            string s => new CkId<TCkKey>(s),
            CkId<TCkKey> _ => value,
            _ => ThrowValueConversionError(value)
        };
    }

    public override object? Serialize(object? value)
    {
        if (value is CkId<TCkKey> ckId)
        {
            return ckId.SemanticVersionedFullName;
        }

        return null;
    }
}