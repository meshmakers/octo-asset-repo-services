using FakeItEasy;
using FluentAssertions;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     CK v2 Phase 0 (AB#5667, contract §4.2 / §8.3): CK interface graph type naming and fields, and the warning
///     that replaces the silent skip when an object type cannot implement a CK interface.
/// </summary>
public class CkInterfaceSchemaTests
{
    [Fact]
    public void CkInterfaceType_HasNoSuffix_AndCarriesSystemFieldsAndMembers()
    {
        var graph = new CkInterfaceGraph(new CkId<CkInterfaceId>("System.Identity/Named-1"),
            "Named things", new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>
            {
                [new CkId<CkAttributeId>("System/Name")] = Attribute("Name", isOptional: false),
                [new CkId<CkAttributeId>("System/Description")] = Attribute("Description", isOptional: true)
            }, []);

        var type = new CkInterfaceGraphType(graph);
        type.Populate(Options.Create(new OctoAssetRepositoryServicesOptions()), A.Fake<IGraphTypesCache>(), graph);

        type.Name.Should().Be("SystemIdentityNamed");
        type.Description.Should().Be("Named things");
        type.Fields.Select(f => f.Name).Should().Contain(["RtId", "CkTypeId", "rtDisplayName", "Name", "Description"]);
        type.Fields.Find("Name")!.Type.Should().Be<NonNullGraphType<StringGraphType>>();
        type.Fields.Find("Description")!.Type.Should().Be<StringGraphType>();
    }

    [Fact]
    public void InterfaceMismatch_IsAWarningForCkInterfaces_AndDebugForAbstractTypeInterfaces()
    {
        var logger = new CapturingLogger();
        var cache = new GraphTypesCache(A.Fake<ICkCacheService>(), A.Fake<IOctoService>(),
            Options.Create(new OctoAssetRepositoryServicesOptions()), "meshtest", logger);
        var ckInterface = new CkInterfaceGraphType(new CkInterfaceGraph(
            new CkId<CkInterfaceId>("Test/Labeled-1"), null,
            new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>(), []));
        var abstractTypeInterface = new InterfaceGraphType { Name = "SystemEntityInterface" };

        cache.ReportInterfaceNotImplemented("TestThing", ckInterface, "field 'label' is missing");
        cache.ReportInterfaceNotImplemented("TestThing", abstractTypeInterface, "field 'x' is missing");

        logger.Entries.Should().HaveCount(2);
        logger.Entries[0].Level.Should().Be(LogLevel.Warning);
        logger.Entries[0].Message.Should().Contain("TestThing").And.Contain("TestLabeled").And
            .Contain("field 'label' is missing");
        logger.Entries[1].Level.Should().Be(LogLevel.Debug);
    }

    private static CkTypeAttributeGraph Attribute(string name, bool isOptional)
    {
        return new CkTypeAttributeGraph(new CkId<CkAttributeId>("System/" + name), name, null,
            AttributeValueTypesDto.String, null, null, null, null, null, isOptional, null);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
