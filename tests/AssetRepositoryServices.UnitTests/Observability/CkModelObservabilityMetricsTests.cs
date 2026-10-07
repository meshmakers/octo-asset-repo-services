using System.Diagnostics.Metrics;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Observability;
using Xunit;

namespace AssetRepositoryServices.UnitTests.Observability;

/// <summary>
///     AB#5432: the instruments are process-wide statics, so every test here resets them and the
///     class is deliberately the only place that touches them (xunit runs a class's tests
///     sequentially).
///
///     What is worth testing is not "does a gauge report a number" but the three things that make
///     this family trustworthy: a disabled sweep publishes nothing at all, a healthy fleet publishes
///     only heartbeats, and a dead sweep stops publishing its last snapshot while the age keeps
///     growing. The third one is the bug from 29.09. that this whole design exists to prevent.
/// </summary>
[Collection("CkModelObservabilityMetrics")]
public class CkModelObservabilityMetricsTests : IDisposable
{
    private readonly MeterListener _listener;
    private readonly List<(string Name, double Value, Dictionary<string, string?> Tags)> _measurements = [];

    public CkModelObservabilityMetricsTests()
    {
        CkModelObservabilityMetrics.ResetForTests();

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CkModelObservabilityMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _listener.SetMeasurementEventCallback<int>(Record);
        _listener.SetMeasurementEventCallback<double>(Record);
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        CkModelObservabilityMetrics.ResetForTests();
    }

    [Fact]
    public void NothingIsPublished_WhileTheSweepIsNotArmed()
    {
        CkModelObservabilityMetrics.SetTenantFaults("acme", [new CkLibraryFault("EnergyIQ", "1.2.0", 2)]);

        Collect();

        // A deployment that switched the sweep off must not produce a dead man's switch that fires
        // forever — so not even the heartbeat or the age may appear.
        _measurements.Should().BeEmpty();
    }

    [Fact]
    public void AHealthyFleetPublishesOnlyHeartbeats()
    {
        CkModelObservabilityMetrics.Arm(TimeSpan.FromMinutes(15));
        CkModelObservabilityMetrics.SetTenantFaults("acme", []);
        CkModelObservabilityMetrics.RecordSweepCompleted();

        Collect();

        Names().Should().BeEquivalentTo(
            CkModelObservabilityMetrics.SweepHeartbeatMetricName,
            CkModelObservabilityMetrics.SweepAgeSecondsMetricName,
            CkModelObservabilityMetrics.TenantObservedMetricName);

        Value(CkModelObservabilityMetrics.SweepHeartbeatMetricName).Should().Be(1);
        Tags(CkModelObservabilityMetrics.TenantObservedMetricName)["octo.tenant.id"].Should().Be("acme");
    }

    [Fact]
    public void AFaultIsPublishedWithTenantLibraryAndVersion()
    {
        CkModelObservabilityMetrics.Arm(TimeSpan.FromMinutes(15));
        CkModelObservabilityMetrics.SetTenantFaults("acme",
        [
            new CkLibraryFault("EnergyIQ", "1.2.0", CkModelObservabilityMetrics.StateResolveFailed),
            new CkLibraryFault("Loxone", "1.1.0", CkModelObservabilityMetrics.StateCatalogInconsistency)
        ]);
        CkModelObservabilityMetrics.RecordSweepCompleted();

        Collect();

        var states = _measurements
            .Where(m => m.Name == CkModelObservabilityMetrics.LibraryStateMetricName)
            .ToList();

        states.Should().HaveCount(2);
        states.Should().ContainSingle(m =>
            m.Value == 2 && m.Tags["octo.ck.library"] == "EnergyIQ" && m.Tags["octo.ck.version"] == "1.2.0" &&
            m.Tags["octo.tenant.id"] == "acme");
        states.Should().ContainSingle(m =>
            m.Value == 1 && m.Tags["octo.ck.library"] == "Loxone" && m.Tags["octo.ck.version"] == "1.1.0");
    }

    [Fact]
    public void AnUnreachableTenantIsPublishedWithoutItsPreviousFaults()
    {
        CkModelObservabilityMetrics.Arm(TimeSpan.FromMinutes(15));
        CkModelObservabilityMetrics.SetTenantFaults("acme", [new CkLibraryFault("EnergyIQ", "1.2.0", 2)]);
        CkModelObservabilityMetrics.SetTenantUnreachable("acme");
        CkModelObservabilityMetrics.RecordSweepCompleted();

        Collect();

        Value(CkModelObservabilityMetrics.TenantUnreachableMetricName).Should().Be(1);
        // An unreadable tenant has no current fault list; republishing the old one would present a
        // stale reading as a fresh measurement.
        Names().Should().NotContain(CkModelObservabilityMetrics.LibraryStateMetricName);
    }

    [Fact]
    public void AnOptedOutTenantDisappearsFromTheExport()
    {
        CkModelObservabilityMetrics.Arm(TimeSpan.FromMinutes(15));
        CkModelObservabilityMetrics.SetTenantFaults("acme", [new CkLibraryFault("EnergyIQ", "1.2.0", 2)]);
        CkModelObservabilityMetrics.RecordSweepCompleted();
        CkModelObservabilityMetrics.Forget("acme");

        Collect();

        Names().Should().NotContain(CkModelObservabilityMetrics.LibraryStateMetricName);
        Names().Should().NotContain(CkModelObservabilityMetrics.TenantObservedMetricName);
        // The sweep itself is still alive, so the heartbeat stays.
        Names().Should().Contain(CkModelObservabilityMetrics.SweepHeartbeatMetricName);
    }

    [Fact]
    public void AStaleSweepStopsPublishingFaultsAndTheHeartbeat_ButNotTheAge()
    {
        // A negative window makes any age stale, which keeps this deterministic instead of sleeping.
        CkModelObservabilityMetrics.Arm(TimeSpan.FromSeconds(-1));
        CkModelObservabilityMetrics.SetTenantFaults("acme", [new CkLibraryFault("EnergyIQ", "1.2.0", 2)]);
        CkModelObservabilityMetrics.RecordSweepCompleted();

        Collect();

        // This is the 29.09. failure: an ObservableGauge callback fires on the exporter's interval,
        // not the sweep's, so without this guard a dead sweep would keep re-publishing its last
        // snapshot and its own heartbeat forever.
        Names().Should().BeEquivalentTo(CkModelObservabilityMetrics.SweepAgeSecondsMetricName);
    }

    [Fact]
    public void TheSweepAgeIsPublishedBeforeTheFirstSweepEverCompletes()
    {
        CkModelObservabilityMetrics.Arm(TimeSpan.FromMinutes(15));

        Collect();

        // "Never ran once" has to be as visible as "stopped running", so the clock starts at arm
        // time rather than at the first completed sweep.
        Names().Should().Contain(CkModelObservabilityMetrics.SweepAgeSecondsMetricName);
        Value(CkModelObservabilityMetrics.SweepAgeSecondsMetricName).Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void AnUnfinishedSweepDoesNotRefreshTheHeartbeat()
    {
        CkModelObservabilityMetrics.Arm(TimeSpan.FromMinutes(15));
        CkModelObservabilityMetrics.RecordSweepCompleted();
        Collect();
        var first = Value(CkModelObservabilityMetrics.SweepAgeSecondsMetricName);

        // Writing tenant results without completing the sweep must not move the clock: a sweep that
        // threw halfway through has not observed the tenants it never reached.
        CkModelObservabilityMetrics.SetTenantFaults("acme", []);
        _measurements.Clear();
        Collect();

        Value(CkModelObservabilityMetrics.SweepAgeSecondsMetricName).Should().BeGreaterThanOrEqualTo(first);
    }

    private void Collect()
    {
        _listener.RecordObservableInstruments();
    }

    private void Record<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state) where T : struct
    {
        var dictionary = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            dictionary[tag.Key] = tag.Value?.ToString();
        }

        _measurements.Add((instrument.Name, Convert.ToDouble(measurement), dictionary));
    }

    private IEnumerable<string> Names() => _measurements.Select(m => m.Name).Distinct();

    private double Value(string name) => _measurements.Single(m => m.Name == name).Value;

    private Dictionary<string, string?> Tags(string name) => _measurements.Single(m => m.Name == name).Tags;
}
