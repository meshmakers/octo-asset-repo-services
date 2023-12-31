using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public class ScopeIdsDtoType : EnumerationGraphType<ScopeIdsDto>
{
    public ScopeIdsDtoType()
    {
        Name = "Scopes";
        Description = "The scope of the construction kit model";
    }
}
