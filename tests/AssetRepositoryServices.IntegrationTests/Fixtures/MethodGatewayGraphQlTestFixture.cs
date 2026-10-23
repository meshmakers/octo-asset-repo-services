using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Dispatching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     AB#7026: the GraphQL host with a dispatcher double behind the real gateway (authorization, target lookup,
///     parameter validation) and a log capture. The handler side is scripted per test through
///     <see cref="Dispatcher" />; everything in front of it is production code.
/// </summary>
public class MethodGatewayGraphQlTestFixture : GraphQlTestFixture
{
    public MethodGatewayGraphQlTestFixture()
    {
        Dispatcher = new ScriptedDispatcher();
        Services.AddSingleton<ICkMethodDispatcher>(Dispatcher);
        Services.AddLogging(b => b.AddProvider(new CapturingLoggerProvider(Logs)));
    }

    public ScriptedDispatcher Dispatcher { get; }

    /// <summary>Every log entry (level, text with exception) of the host.</summary>
    public List<(LogLevel Level, string Text)> Logs { get; } = [];

    public sealed class ScriptedDispatcher : ICkMethodDispatcher
    {
        private readonly List<CkMethodRequest> _requests = [];

        /// <summary>Answer of the next calls; default is a success without result.</summary>
        public Func<CkMethodRequest, CkMethodResponse> Script { get; set; } = DefaultScript;

        public IReadOnlyList<CkMethodRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToList();
                }
            }
        }

        public void Reset()
        {
            lock (_requests)
            {
                _requests.Clear();
            }

            Script = DefaultScript;
        }

        public Task<CkMethodResponse> InvokeAsync(CkMethodRequest request, CancellationToken cancellationToken = default)
        {
            lock (_requests)
            {
                _requests.Add(request);
            }

            return Task.FromResult(Script(request));
        }

        private static CkMethodResponse DefaultScript(CkMethodRequest request) =>
            CkMethodResponse.Succeeded(request.InvocationId, "handler-1");
    }

    private sealed class CapturingLoggerProvider(List<(LogLevel, string)> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(sink);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<(LogLevel, string)> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (sink)
                {
                    sink.Add((logLevel, $"{formatter(state, exception)} {exception}"));
                }
            }
        }
    }
}
