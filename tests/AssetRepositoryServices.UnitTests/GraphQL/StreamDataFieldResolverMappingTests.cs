using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
/// Pins the simple-query wire contract: <c>ResolveToMappings</c> emits the caller's requested
/// column string verbatim as <c>ColumnNameMapping.Wire</c> (the value that surfaces as
/// <c>cell.attributePath</c> on the GraphQL wire). Without this, Refinery Studio's query result
/// grid binds Kendo's <c>field</c> to the saved query columns but receives row values keyed by
/// the lower-case CrateDB column name — cells render empty while the row count is correct.
/// </summary>
public class StreamDataFieldResolverMappingTests
{
    private readonly StreamDataFieldResolver _resolver = new(
        ["Voltage", "ObisCode", "DataQuality", "Sensor.Reading.Value"]);

    [Fact]
    public void ResolveToMappings_EchoesRequestedColumnAsWire()
    {
        var mappings = _resolver.ResolveToMappings(
            ["Voltage", "ObisCode", "DataQuality"]);

        // Wire alias must echo the caller's input so client grids can bind directly to saved query columns.
        mappings.Select(m => m.Wire).Should().Equal("Voltage", "ObisCode", "DataQuality");
    }

    [Fact]
    public void ResolveToMappings_CanonicalIsLowerCaseConcatStorageKey()
    {
        var mappings = _resolver.ResolveToMappings(
            ["Voltage", "ObisCode", "Sensor.Reading.Value"]);

        // Canonical = CrateDB column name = lower-case concatenated (see ColumnNameMapper).
        // The wire form is decoupled from this — it must echo the input.
        mappings.Select(m => m.Canonical).Should().Equal("voltage", "obiscode", "sensorreadingvalue");
    }

    [Fact]
    public void ResolveToMappings_PreservesInputCasingAcrossVariants()
    {
        // Same underlying CK attribute, three different request casings → three different wires.
        // The resolver lookup itself is case-insensitive (StreamDataFieldResolver uses
        // OrdinalIgnoreCase), so all three resolve; the wire echoes the input verbatim.
        // Canonical lookup key stays the CrateDB column name regardless of input casing.
        var mappings = _resolver.ResolveToMappings(
            ["ObisCode", "obisCode", "obiscode"]);

        mappings.Select(m => m.Wire).Should().Equal("ObisCode", "obisCode", "obiscode");
        mappings.Select(m => m.Canonical).Should().AllBe("obiscode");
    }

    [Fact]
    public void ResolveToMappings_DefaultFieldsAlsoEchoInput()
    {
        var mappings = _resolver.ResolveToMappings(["timestamp", "rtId"]);

        mappings.Select(m => m.Wire).Should().Equal("timestamp", "rtId");
    }
}
