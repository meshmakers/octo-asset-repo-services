using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     Credential guard regression tests, blueprint seed: the blueprint flow fixture (real blueprint service and MongoDB,
///     local file system blueprint catalog on <c>TestBlueprints/</c>, no catalog writes) with the real System.Identity model
///     in the embedded CK catalog and a log capture.
/// </summary>
public class IdentitySeedBlueprintFixture : BlueprintUpdateFlowFixture
{
    public IdentitySeedBlueprintFixture()
    {
        Services.AddSystemIdentitySnapshotCkModel();
        Services.AddLogging(b => b.AddProvider(new ListLoggerProvider(Logs)));
    }

    /// <summary>Every log line of the host (text with exception).</summary>
    public List<string> Logs { get; } = [];

    private sealed class ListLoggerProvider(List<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(sink);

        public void Dispose()
        {
        }

        private sealed class ListLogger(List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (sink)
                {
                    sink.Add($"{formatter(state, exception)} {exception}");
                }
            }
        }
    }
}
