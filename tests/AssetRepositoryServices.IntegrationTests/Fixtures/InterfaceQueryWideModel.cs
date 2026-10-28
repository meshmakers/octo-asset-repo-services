using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     CK language 2 model with <c>rootCount</c> independent collection roots <c>T01..Tnn</c>, all implementing
///     <c>Named-1</c> (Name, Rank): the fan-out cap test (65 roots) and the performance test (30 roots, with or without
///     an ascending index on Name) of the interface query (AB#6932, copied from the engine-mongodb test model of AB#7019).
/// </summary>
internal static class InterfaceQueryWideModel
{
    internal static CkModelId ModelIdOf(string name) => new($"{name}-1.0.0");

    internal static string TypeName(string modelName, int index) => $"{modelName}/T{index:D2}";

    internal static CkCompiledModelRoot Build(CkModelId systemId, string name, int rootCount, bool indexName,
        bool soloInterface = false)
    {
        var id = ModelIdOf(name);
        CkId<CkAttributeId> Attribute(string attribute) => new(id, new CkAttributeId($"{attribute}-1"));

        CkTypeAttributeDto Assign(string attribute, bool isOptional) => new()
        {
            CkAttributeId = Attribute(attribute), AttributeName = attribute, IsOptional = isOptional
        };

        List<CkId<CkInterfaceId>> ImplementsOf(int index)
        {
            var implements = new List<CkId<CkInterfaceId>> { new(id, new CkInterfaceId("Named-1")) };
            if (index == 1 && soloInterface)
            {
                implements.Add(new CkId<CkInterfaceId>(id, new CkInterfaceId("Solo-1")));
            }

            return implements;
        }

        CkInterfaceDto InterfaceOf(string interfaceName) => new()
        {
            InterfaceId = new CkInterfaceId($"{interfaceName}-1"),
            Attributes =
            [
                new CkInterfaceAttributeDto { CkAttributeId = Attribute("Name"), AttributeName = "Name" },
                new CkInterfaceAttributeDto { CkAttributeId = Attribute("Rank"), AttributeName = "Rank", IsOptional = true }
            ]
        };

        var interfaces = new List<CkInterfaceDto> { InterfaceOf("Named") };
        if (soloInterface)
        {
            interfaces.Add(InterfaceOf("Solo"));
        }

        var types = Enumerable.Range(1, rootCount).Select(i => new CkCompiledTypeDto
        {
            TypeId = new CkTypeId($"T{i:D2}-1"),
            IsCollectionRoot = true,
            DerivedFromCkTypeId = new CkId<CkTypeId>(systemId, new CkTypeId("Entity-1")),
            Implements = ImplementsOf(i),
            Attributes = [Assign("Name", false), Assign("Rank", true)],
            Indexes = indexName
                ?
                [
                    new CkTypeIndexDto
                    {
                        IndexType = IndexTypeDto.Ascending,
                        Fields = [new CkIndexFieldsDto { AttributePaths = ["Name"] }]
                    }
                ]
                : null
        }).ToList();

        return new CkCompiledModelRoot
        {
            ModelId = id,
            CkLanguage = 2,
            Dependencies = [systemId],
            Attributes =
            [
                new CkAttributeDto { AttributeId = new CkAttributeId("Name-1"), ValueType = AttributeValueTypesDto.String },
                new CkAttributeDto { AttributeId = new CkAttributeId("Rank-1"), ValueType = AttributeValueTypesDto.Integer }
            ],
            Interfaces = interfaces,
            Types = types.Cast<CkCompiledTypeDto>().ToList()
        };
    }
}
