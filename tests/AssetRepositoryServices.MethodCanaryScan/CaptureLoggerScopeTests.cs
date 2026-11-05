using Microsoft.Extensions.Logging;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.MethodCanaryScan;

/// <summary>
///     The scopes of the handler-host capture logger are per async flow: forked flows see their parent's scopes, never each
///     other's, and disposing a scope in one flow does not change what another flow logs. Runs without the local stack.
/// </summary>
public sealed class CaptureLoggerScopeTests : IDisposable
{
    private readonly string _logFile = Path.Combine(Path.GetTempPath(), $"capture-logger-scope-{Guid.NewGuid():N}.log");

    public void Dispose() => File.Delete(_logFile);

    [Fact]
    public void NestedScopes_AreLoggedOutermostFirst_AndRemovedOnDispose()
    {
        var sink = new CaptureSink(_logFile);
        var logger = sink.CreateLogger("test");

        using (logger.BeginScope("outer"))
        {
            using (logger.BeginScope("inner"))
            {
                logger.LogInformation("both");
            }

            logger.LogInformation("outer-only");
        }

        logger.LogInformation("none");

        var texts = sink.Texts.Select(t => t.Text).ToList();
        Assert.Contains(": both ", texts[0]);
        Assert.EndsWith("=both |scope outer |scope inner", texts[0]);
        Assert.EndsWith("=outer-only |scope outer", texts[1]);
        Assert.DoesNotContain("|scope", texts[2]);
    }

    [Fact]
    public async Task ForkedFlows_SeeOnlyTheirOwnScopes()
    {
        var sink = new CaptureSink(_logFile);
        var logger = sink.CreateLogger("test");

        using (logger.BeginScope("parent"))
        {
            await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
            {
                for (var round = 0; round < 50; round++)
                {
                    using (logger.BeginScope($"flow-{i}"))
                    {
                        await Task.Yield();
                        logger.LogInformation("in-{Flow}", i);
                    }
                }
            })));

            logger.LogInformation("after");
        }

        var texts = sink.Texts.Select(t => t.Text).ToList();
        Assert.Equal(32 * 50 + 1, texts.Count);
        foreach (var text in texts.Where(t => t.Contains(": in-", StringComparison.Ordinal)))
        {
            var flow = text[(text.IndexOf(": in-", StringComparison.Ordinal) + 5)..].Split(' ')[0];
            Assert.EndsWith($"|scope parent |scope flow-{flow}", text);
        }

        Assert.EndsWith("=after |scope parent", texts.Single(t => t.Contains(": after", StringComparison.Ordinal)));
    }
}
