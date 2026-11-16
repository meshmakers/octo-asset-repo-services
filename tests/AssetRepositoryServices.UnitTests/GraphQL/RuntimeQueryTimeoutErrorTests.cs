using System.Collections.Concurrent;
using FakeItEasy;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     A runtime query that MongoDB stopped at its time limit reaches the client as the coded error ASSET1008 (GraphQL)
///     or 504 problem details (REST) with CK type, limit and error id, and is logged once as a warning with tenant,
///     CK type and duration - never as the generic ASSET1002.
/// </summary>
public class RuntimeQueryTimeoutErrorTests
{
    private const string TenantId = "tenant-a";
    private const string CkTypeId = "Test/Machine-1";

    private static RuntimeQueryTimeoutException Timeout() =>
        new(TenantId, CkTypeId, TimeSpan.FromSeconds(120), TimeSpan.FromMilliseconds(120_015), null);

    [Fact]
    public void GraphQl_HandleException_MapsToAsset1008WithCkTypeLimitAndErrorId()
    {
        var logs = new CapturingLoggerProvider();
        var context = CreateContext(logs, out var errors);

        context.HandleException(Timeout());

        var error = errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(Statics.GraphQlRuntimeQueryTimeout).And.Be("ASSET1008");
        var errorId = (string)error.Extensions![RuntimeQueryTimeoutError.ErrorIdKey]!;
        errorId.Should().NotBeNullOrEmpty();
        error.Message.Should().Contain($"'{CkTypeId}'").And.Contain("120000 ms").And.Contain(errorId);
        var parameters = (Dictionary<string, object?>)error.Extensions["parameters"]!;
        parameters["ckTypeId"].Should().Be(CkTypeId);
        parameters["maxTimeMs"].Should().Be(120_000L);
        parameters["elapsedMs"].Should().Be(120_015L);

        var log = logs.Entries.Should().ContainSingle().Subject;
        log.Level.Should().Be(LogLevel.Warning);
        log.Message.Should().Contain(TenantId).And.Contain(CkTypeId).And.Contain("120015 ms").And.Contain(errorId);
    }

    [Fact]
    public void GraphQl_HandleException_FindsAWrappedTimeout()
    {
        var context = CreateContext(new CapturingLoggerProvider(), out var errors);

        context.HandleException(new InvalidOperationException("outer",
            new AggregateException(new ArgumentException("other"), Timeout())));

        errors.Should().ContainSingle().Which.Code.Should().Be("ASSET1008");
    }

    [Fact]
    public void Rest_Filter_Answers504ProblemDetailsWithTheCode()
    {
        var logs = new CapturingLoggerProvider();
        var context = new ExceptionContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), [])
        {
            Exception = Timeout()
        };

        new RuntimeQueryTimeoutExceptionFilter(LoggerFactory.Create(b => b.AddProvider(logs))).OnException(context);

        context.ExceptionHandled.Should().BeTrue();
        var result = context.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status504GatewayTimeout);
        var problem = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Type.Should().Be(RuntimeQueryTimeoutError.ProblemType);
        problem.Extensions["code"].Should().Be("ASSET1008");
        problem.Extensions["ckTypeId"].Should().Be(CkTypeId);
        problem.Extensions["maxTimeMs"].Should().Be(120_000L);
        problem.Detail.Should().Contain((string)problem.Extensions[RuntimeQueryTimeoutError.ErrorIdKey]!);
        logs.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public void Rest_Filter_LeavesOtherExceptionsAlone()
    {
        var context = new ExceptionContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), [])
        {
            Exception = new InvalidOperationException("x")
        };

        new RuntimeQueryTimeoutExceptionFilter(LoggerFactory.Create(_ => { })).OnException(context);

        context.ExceptionHandled.Should().BeFalse();
        context.Result.Should().BeNull();
    }

    [Fact]
    public void TryFind_IsFalseWithoutATimeout()
    {
        RuntimeQueryTimeoutError.TryFind(new InvalidOperationException("x", new TimeoutException()), out var timeout)
            .Should().BeFalse();
        timeout.Should().BeNull();
    }

    private static IResolveFieldContext CreateContext(CapturingLoggerProvider logs, out ExecutionErrors errors)
    {
        var context = A.Fake<IResolveFieldContext>();
        var contextErrors = new ExecutionErrors();
        errors = contextErrors;
        A.CallTo(() => context.Errors).Returns(contextErrors);
        var services = new ServiceCollection().AddLogging(b => b.AddProvider(logs)).BuildServiceProvider();
        A.CallTo(() => context.RequestServices).Returns(services);
        return context;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries.ToList();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                provider._entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
