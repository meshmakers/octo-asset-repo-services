using GraphQL.Types;
using Meshmakers.Octo.Runtime.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;

internal class AssociationModOptionsDtoType : EnumerationGraphType<AssociationModOptionsDto>
{
    public AssociationModOptionsDtoType()
    {
        Name = "AssociationModOptions";
        Description = "Defines the type of modification during write operations";
    }
}