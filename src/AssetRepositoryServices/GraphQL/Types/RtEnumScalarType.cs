using GraphQL;
using GraphQL.DataLoader;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

public class RtEnumScalarType : EnumerationGraphType
{
    public RtEnumScalarType(CkId<CkEnumId> ckEnumId)
    {
        CkEnumId = ckEnumId;
        Name = ckEnumId.GetGraphQlName();
        Description = $"Runtime entities of construction kit enum '{ckEnumId}'";
    }

    public CkId<CkEnumId> CkEnumId { get; }

    internal void Populate(ICkCacheService ckCacheService, string tenantId, IGraphTypesCache graphTypesCache,
        IDataLoaderContextAccessor dataLoaderAccessor,
        IOctoSessionAccessor sessionAccessor, CkEnumGraph ckEnumGraph)
    {
        var enumGraphData = ckEnumGraph.Values.Select(e => (
            name: e.Name.ToConstantCase(),
            value: e.Key,
            description: e.Description
        ));

        foreach (var (name, value, description) in enumGraphData)
        {
            var enumValue = new EnumValueDefinition(name, value)
            {
                Description = description
            };

            Add(enumValue);
        }
    }
}