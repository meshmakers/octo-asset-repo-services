using System.Collections.Concurrent;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Audit;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     The REST lifecycle endpoints and the deprecated mutations as facades of the Archive methods, against the real
///     host of <see cref="ArchiveMethodStreamDataFixture" /> (MongoDB, CrateDB, gateway, invocation pipeline, in-process
///     handlers). Two observers are added: every Method audit event (<see cref="AuditEvents" />) and every answer of the
///     request-session tripwire at handler entry (<see cref="SessionVisibleAtHandlerEntry" />).
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class ArchiveMethodFacadeFixture : ArchiveMethodStreamDataFixture
{
    public ArchiveMethodFacadeFixture()
    {
        // the last registration wins: the pipeline filter and the gateway write into the list below
        Services.AddSingleton<ICkMethodAuditRecorder>(new CapturingAuditRecorder(AuditEvents));
        Services.AddSingleton<ICkInProcessAmbientStateProbe>(
            new RecordingProbe(new OctoSessionAmbientStateProbe(), SessionVisibleAtHandlerEntry));
    }

    /// <summary>The Method audit events in the order they were written.</summary>
    public ConcurrentQueue<CkMethodAuditRecord> AuditEvents { get; } = new();

    /// <summary>What the tripwire of the in-process transport saw at handler entry, per call (<c>true</c> = a request session).</summary>
    public ConcurrentQueue<bool> SessionVisibleAtHandlerEntry { get; } = new();

    private sealed class CapturingAuditRecorder(ConcurrentQueue<CkMethodAuditRecord> sink) : ICkMethodAuditRecorder
    {
        public Task RecordAsync(CkMethodAuditRecord record, CancellationToken cancellationToken = default)
        {
            sink.Enqueue(record);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProbe(ICkInProcessAmbientStateProbe inner, ConcurrentQueue<bool> sink)
        : ICkInProcessAmbientStateProbe
    {
        public bool IsRequestStateVisible(IServiceProvider handlerServices)
        {
            var visible = inner.IsRequestStateVisible(handlerServices);
            sink.Enqueue(visible);
            return visible;
        }
    }
}
