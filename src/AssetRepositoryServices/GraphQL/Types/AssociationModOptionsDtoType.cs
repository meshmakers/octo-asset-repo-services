using GraphQL.Types;
using Meshmakers.Octo.Runtime.Contracts;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public class AssociationModOptionsDtoType : EnumerationGraphType<AssociationModOptionsDto>
{
    public AssociationModOptionsDtoType()
    {
        Name = "AssociationModOptions";
        Description = "Defines the type of modification during write operations";
    }
}