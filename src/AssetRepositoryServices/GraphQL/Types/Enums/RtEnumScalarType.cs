using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;

[DoNotRegister]
internal class RtEnumScalarType : EnumerationGraphType
{
    public RtEnumScalarType(RtCkId<CkEnumId> ckEnumId)
    {
        CkEnumId = ckEnumId;
        Name = ckEnumId.GetGraphQlPascalCaseName();
        Description = $"Runtime entities of construction kit enum '{ckEnumId}'";
    }

    public RtCkId<CkEnumId> CkEnumId { get; }

    internal void Populate(CkEnumGraph ckEnumGraph)
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