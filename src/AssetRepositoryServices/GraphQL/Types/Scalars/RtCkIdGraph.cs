using GraphQL.Types;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;

internal class RtCkIdGraph<TCkKey> : ScalarGraphType where TCkKey : IComparable<TCkKey>, ICkElementId
{
    public RtCkIdGraph()
    {
        Name = "Rt" + typeof(TCkKey).Name;
        Description = "A runtime construction kit id of " + typeof(TCkKey).Name + ".";
    }

    public override object? ParseValue(object? value)
    {
        if (value == null)
        {
            return null;
        }

        return value switch
        {
            string s => new RtCkId<TCkKey>(s),
            RtCkId<TCkKey> _ => value,
            _ => ThrowValueConversionError(value)
        };
    }

    public override object? Serialize(object? value)
    {
        if (value is RtCkId<TCkKey> rtCkId)
        {
            return rtCkId.SemanticVersionedFullName;
        }

        return null;
    }
}