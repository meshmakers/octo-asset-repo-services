using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Observability;
using Xunit;

namespace AssetRepositoryServices.UnitTests.Observability;

/// <summary>
///     AB#5432: the fault classification is the only part of the CK model health sweep with
///     judgement in it, and the metric names and value scale are a contract that three already
///     deployed check rules depend on. These tests pin the contract.
/// </summary>
public class CkModelHealthEvaluatorTests
{
    [Fact]
    public void Evaluate_ReportsNothing_ForAHealthyFleet()
    {
        var status = Status(
            new CkModelLibraryStatusItemDto
            {
                Name = "EnergyIQ", InstalledVersion = "1.2.0", ModelState = "Available", CatalogVersion = "1.2.0"
            },
            new CkModelLibraryStatusItemDto
            {
                Name = "System", InstalledVersion = "2.3.0", ModelState = "Available", IsServiceManaged = true
            });

        // Fault-only is the whole point: a healthy library must cost nothing on the wire.
        CkModelHealthEvaluator.Evaluate(status).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_ReportsResolveFailedAsCritical_WithTheInstalledVersion()
    {
        var status = Status(new CkModelLibraryStatusItemDto
        {
            Name = "EnergyIQ",
            InstalledVersion = "1.2.0",
            ModelState = "ResolveFailed",
            CatalogVersion = "1.3.0"
        });

        var faults = CkModelHealthEvaluator.Evaluate(status);

        faults.Should().HaveCount(1);
        faults[0].Library.Should().Be("EnergyIQ");
        faults[0].State.Should().Be(CkModelObservabilityMetrics.StateResolveFailed);
        // The installed version is what is broken; the catalog version is only the candidate repair.
        faults[0].Version.Should().Be("1.2.0");
    }

    [Fact]
    public void Evaluate_ReportsResolveFailedForServiceManagedModelsToo()
    {
        var status = Status(new CkModelLibraryStatusItemDto
        {
            Name = "System.Communication",
            InstalledVersion = "3.36.0",
            ModelState = "ResolveFailed",
            IsServiceManaged = true
        });

        // A broken System model is the most severe case there is, not an exempt one.
        CkModelHealthEvaluator.Evaluate(status).Should().ContainSingle()
            .Which.State.Should().Be(CkModelObservabilityMetrics.StateResolveFailed);
    }

    [Fact]
    public void Evaluate_ReportsCatalogInconsistencyAsWarning_WithTheCatalogVersion()
    {
        var status = Status(new CkModelLibraryStatusItemDto
        {
            Name = "Loxone",
            InstalledVersion = "1.0.0",
            ModelState = "Available",
            CatalogVersion = "1.1.0",
            HasCatalogInconsistency = true,
            UnresolvedDependencies = ["EnergyIQ-2.0.0"]
        });

        var faults = CkModelHealthEvaluator.Evaluate(status);

        faults.Should().HaveCount(1);
        faults[0].State.Should().Be(CkModelObservabilityMetrics.StateCatalogInconsistency);
        // The catalog version is the unpublishable one, so that is the version the fault is about.
        faults[0].Version.Should().Be("1.1.0");
    }

    [Fact]
    public void Evaluate_IgnoresCatalogInconsistency_ForAModelTheTenantNeverInstalled()
    {
        var status = Status(new CkModelLibraryStatusItemDto
        {
            Name = "Loxone",
            InstalledVersion = null,
            CatalogVersion = "1.1.0",
            HasCatalogInconsistency = true,
            UnresolvedDependencies = ["EnergyIQ-2.0.0"]
        });

        // One broken publish in a shared catalog would otherwise be reported once per opted-in
        // tenant, which says something the series does not mean and scales cardinality with the
        // catalog rather than with the fleet.
        CkModelHealthEvaluator.Evaluate(status).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_ReportsOneFaultPerLibrary_HighestSeverityWins()
    {
        var status = Status(new CkModelLibraryStatusItemDto
        {
            Name = "EnergyIQ",
            InstalledVersion = "1.2.0",
            ModelState = "ResolveFailed",
            CatalogVersion = "1.3.0",
            HasCatalogInconsistency = true,
            UnresolvedDependencies = ["Loxone-9.9.9"]
        });

        // Two series for one library with two different values reads as a contradiction; the
        // condition that is actually broken in the tenant wins.
        var faults = CkModelHealthEvaluator.Evaluate(status);
        faults.Should().ContainSingle();
        faults[0].State.Should().Be(CkModelObservabilityMetrics.StateResolveFailed);
        faults[0].Version.Should().Be("1.2.0");
    }

    [Fact]
    public void Evaluate_FallsBackToUnknown_WhenNoVersionIsKnownAtAll()
    {
        var status = Status(new CkModelLibraryStatusItemDto { Name = "Broken", ModelState = "ResolveFailed" });

        // A missing label would silently collapse distinct series; an explicit value keeps the row
        // visible and obviously odd.
        CkModelHealthEvaluator.Evaluate(status).Should().ContainSingle()
            .Which.Version.Should().Be("unknown");
    }

    private static CkModelLibraryStatusResponseDto Status(params CkModelLibraryStatusItemDto[] items) =>
        new() { Items = [..items] };
}
