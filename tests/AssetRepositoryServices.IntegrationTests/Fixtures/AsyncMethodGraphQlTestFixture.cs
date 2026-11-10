using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Idempotency;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     AB#8444 (F3.6): the production host of the asset repository (real schema and MongoDB, real gateway, dispatcher,
///     invocation pipeline and authorization, the production registration of <c>runtime.operations</c> with the
///     <b>MongoDB operation store</b> of AB#8443 in the system database) plus, test-only, an in-process handler for the
///     test model's asynchronous <c>AsyncOperationThing.Export-1</c>, the operation runner of the handler host
///     (<c>AddCkMethodOperations</c>) and the idempotency filter with the in-memory key store (the key store has its own
///     tests). A bus double that must never be touched.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class AsyncMethodGraphQlTestFixture : GraphQlTestFixture
{
    public const string MethodId = "AssetRepositoryIntegrationTest/AsyncOperationThing.Export-1";

    public const string DeclaredError = "EXPORT_FAILED";

    public AsyncMethodGraphQlTestFixture()
    {
        Services.AddSingleton(A.Fake<IDistributionEventHubService>());
        Services.AddSingleton<ExportObservation>();
        Services.AddGatewayOwnedCkMethodHandlers(m => m.Handle<SampleExportHandler>(MethodId));
        Services.Configure<CkMethodHandlerOptions>(o => o.ValidationTenantIds = [SystemTenantId]);
        // the handler host side of AB#8443: records the operation before the handle is returned and runs the work once
        Services.AddCkMethodOperations();
        Services.AddCkMethodIdempotency();
        Services.AddCkMethodInMemoryIdempotencyStore();
        Services.Configure<CkMethodFingerprintOptions>(o =>
        {
            o.Current = new CkMethodFingerprintKeyOptions
            {
                Id = "k-test",
                Secret = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 7)).ToArray())
            };
        });
    }

    public ExportObservation Observation => GetService<ExportObservation>();

    /// <summary>The operation store the host uses (the MongoDB store of the production registration).</summary>
    public ICkMethodOperationStore OperationStore => GetService<ICkMethodOperationStore>();

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        // the startup validation of the in-process registration (as GatewayOwnedMethodGraphQlTestFixture)
        var methodServices = Services
            .Where(d => d.ServiceType == typeof(IAsyncInitializationService) &&
                        d.ImplementationType?.Namespace == typeof(CkMethodHandlerOptions).Namespace)
            .Select(d => (IAsyncInitializationService)ActivatorUtilities.CreateInstance(Provider!, d.ImplementationType!))
            .OrderBy(x => x.Order);
        foreach (var service in methodServices)
        {
            await service.InitializeAsync();
        }
    }

    public sealed class ExportObservation
    {
        private int _accepts;
        private int _works;

        public int Accepts => Volatile.Read(ref _accepts);

        public int Works => Volatile.Read(ref _works);

        /// <summary>Released by a test to let a <c>hold</c> operation finish.</summary>
        public TaskCompletionSource Gate { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Reset()
        {
            Volatile.Write(ref _accepts, 0);
            Volatile.Write(ref _works, 0);
            Gate.TrySetResult();
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Accept() => Interlocked.Increment(ref _accepts);

        /// <summary>Labels of the work runs; tests count their own (unique) label, as works of earlier tests may still end.</summary>
        public global::System.Collections.Concurrent.ConcurrentBag<string> WorkLabels { get; } = [];

        public void Work(string label)
        {
            Interlocked.Increment(ref _works);
            WorkLabels.Add(label);
        }

        public int WorksOf(string label) => WorkLabels.Count(l => l == label);
    }

    public sealed record ExportParameters
    {
        public string? Label { get; init; }
    }

    /// <summary>The result record (the generated <c>{Type}{Method}Result</c> of a real model has the same shape).</summary>
    public sealed record ExportResult(string Value);

    /// <summary>
    ///     Accepts every call. The label selects the work: <c>fail</c> ends with the declared error, <c>hold</c> reports
    ///     progress and waits for <see cref="ExportObservation.Gate" />, anything else succeeds with <c>export-{label}.zip</c>.
    /// </summary>
    public sealed class SampleExportHandler(ExportObservation observation) : ICkMethodHandler<ExportParameters, ExportResult>
    {
        public Task<CkMethodOutcome<ExportResult>> HandleAsync(CkMethodContext<ExportParameters> context,
            CancellationToken cancellationToken)
        {
            observation.Accept();
            var label = context.Parameters.Label ?? "default";
            var gate = observation.Gate;
            return Task.FromResult(CkMethodOutcome<ExportResult>.Accepted(async (progress, ct) =>
            {
                observation.Work(label);
                switch (label)
                {
                    case "fail":
                        return CkMethodOutcome<ExportResult>.Fail(DeclaredError, ("stage", "COPY"));
                    case "hold":
                        await progress.ReportAsync(CkMethodOperationProgress.Create(40, "EXPORTING", ("done", "4"),
                            ("total", "10")), ct);
                        await gate.Task.WaitAsync(ct);
                        break;
                }

                return CkMethodOutcome<ExportResult>.Ok(new ExportResult($"export-{label}.zip"));
            }));
        }
    }
}
