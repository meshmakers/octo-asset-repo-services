using GraphQL.Types;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;

internal class CkVersionType : ScalarGraphType
{
    public CkVersionType()
    {
        Name = nameof(CkVersion);
        Description = "A construction kit version.";
    }

    public override object? ParseValue(object? value)
    {
        return value switch
        {
            string s => new CkVersion(s),
            CkVersion _ => value,
            _ => ThrowValueConversionError(value)
        };
    }

    public override object? Serialize(object? value)
    {
        if (value is CkVersion ckVersion)
        {
            return ckVersion.ToString();
        }

        return null;
    }
}