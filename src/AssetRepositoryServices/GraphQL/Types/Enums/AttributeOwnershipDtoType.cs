using GraphQL.Types;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;

internal class AttributeOwnershipDtoType : EnumerationGraphType<AttributeOwnershipDto>
{
    public AttributeOwnershipDtoType()
    {
        Name = "AttributeOwnership";
        Description =
            "Who owns an attribute value, and therefore what installing the blueprint again does to it (AB#5187). " +
            "SEED_OWNED: the blueprint owns the value - re-applying the blueprint OVERWRITES whatever the tenant has, " +
            "and the value is carried in an exported runtime model. " +
            "TENANT_OWNED: the tenant owns the value - re-applying the blueprint KEEPS the tenant's value and only " +
            "seeds it on a fresh tenant, and the value IS still carried in an exported runtime model. " +
            "RUNTIME_STATE: a service, operator, pipeline or job writes the value - re-applying the blueprint KEEPS " +
            "it, and it is NOT carried in an exported runtime model. " +
            "SECRET: a credential such as an API key, client secret, token or password - re-applying the blueprint " +
            "KEEPS it, and it is NOT carried in an exported runtime model.";
    }
}
