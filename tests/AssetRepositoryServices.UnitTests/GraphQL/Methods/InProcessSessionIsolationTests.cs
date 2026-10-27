using FakeItEasy;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL.Methods;

/// <summary>
///     AB#7023 (OD-8 part 2): with the real <see cref="OctoSessionAccessor" /> and the real in-process transport, a
///     handler started from a flow that holds the request session never sees it, cannot end it, gets its own scoped
///     repositories, and the asset-repo probe is the tripwire when the session would flow into the handler anyway.
/// </summary>
public class InProcessSessionIsolationTests
{
    private const string MethodId = "Sample.Accounts/Account.Touch-1";

    public sealed record TouchParameters
    {
        public string? Note { get; init; }
    }

    public sealed record TouchResult
    {
        public string? Value { get; init; }
    }

    /// <summary>Stand-in for a request-scoped repository: remembers whether it was created inside a session flow.</summary>
    private sealed class SessionBoundRepository(IOctoSessionAccessor accessor)
    {
        public bool CreatedWithSession { get; } = accessor.HasSession;
    }

    private sealed class Observation
    {
        public int HandlerConstructed;
        public int BodyRan;
        public bool? HasSessionAtEntry;
        public bool? HasSessionAfterAwait;
        public SessionBoundRepository? Repository;
    }

    private sealed class TouchHandler(IOctoSessionAccessor accessor, SessionBoundRepository repository, Observation observation)
        : ICkMethodHandler<TouchParameters, TouchResult>
    {
        public async Task<CkMethodOutcome<TouchResult>> HandleAsync(CkMethodContext<TouchParameters> context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref observation.BodyRan);
            observation.HasSessionAtEntry = accessor.HasSession;
            observation.Repository = repository;
            await Task.Yield();
            observation.HasSessionAfterAwait = accessor.HasSession;

            if (context.Parameters.Note == "end-session")
            {
                // A handler must not be able to end the request transaction.
                await accessor.CommitAsync();
                await accessor.AbortAsync();
            }

            return new TouchResult { Value = "done" };
        }
    }

    private sealed class SessionFlowsIntoHandlerAccessor : IOctoSessionAccessor
    {
        public IOctoSession Session { get; set; } = null!;

        public bool HasSession => true; // the regression the tripwire guards against

        public Task AbortAsync() => Task.CompletedTask;

        public Task CommitAsync() => Task.CompletedTask;
    }

    private static (ServiceProvider Provider, Observation Observation) Build(IOctoSessionAccessor accessor)
    {
        var observation = new Observation();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(observation);
        services.AddSingleton(accessor);
        services.AddScoped(sp =>
        {
            Interlocked.Increment(ref sp.GetRequiredService<Observation>().HandlerConstructed);
            return new TouchHandler(sp.GetRequiredService<IOctoSessionAccessor>(),
                sp.GetRequiredService<SessionBoundRepository>(), sp.GetRequiredService<Observation>());
        });
        services.AddScoped<SessionBoundRepository>();
        services.AddFakeMethodDefinition(MethodId);
        services.AddCkInProcessMethodHandlers(m => m.Handle<TouchHandler>(MethodId));
        services.AddOctoSessionAmbientStateProbe();
        var provider = services.BuildServiceProvider();
        provider.InitializeMethodsAsync().GetAwaiter().GetResult();
        return (provider, observation);
    }

    private static CkMethodRequest Request(string? note = null) => new("meshtest")
    {
        InvocationId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        MethodId = MethodId,
        CkTypeId = "Sample.Accounts/Account",
        TargetRtId = "65f0c0ffee",
        ParametersJson = note == null ? "{}" : $"{{\"note\":\"{note}\"}}",
        CallerAccessToken = "token",
        Caller = new CkMethodCaller { SubjectId = "sub-1" },
        DeadlineUtc = DateTime.UtcNow.AddSeconds(30)
    };

    private static IOctoSession FakeSession() => A.Fake<IOctoSession>();

    [Fact]
    public async Task HandlerStartedInsideARequestSession_SeesNoSession_AndTheRequestSessionStaysUsable()
    {
        var accessor = new OctoSessionAccessor();
        var (provider, observation) = Build(accessor);
        var session = FakeSession();
        accessor.Session = session; // what OctoSessionListener does before a mutation
        Assert.True(accessor.HasSession);

        var response = await provider.GetRequiredService<ICkInProcessMethodTransport>()
            .InvokeAsAuthorizedCallerAsync(Request(), TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Equal(1, observation.BodyRan);
        Assert.False(observation.HasSessionAtEntry);
        Assert.False(observation.HasSessionAfterAwait);
        Assert.True(accessor.HasSession);
        Assert.Same(session, accessor.Session);
        A.CallTo(() => session.CommitTransactionAsync()).MustNotHaveHappened();

        await accessor.CommitAsync(); // the request can still commit
        A.CallTo(() => session.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task HandlerCannotCommitOrAbortTheRequestSession()
    {
        var accessor = new OctoSessionAccessor();
        var (provider, observation) = Build(accessor);
        var session = FakeSession();
        accessor.Session = session;

        var response = await provider.GetRequiredService<ICkInProcessMethodTransport>()
            .InvokeAsAuthorizedCallerAsync(Request("end-session"), TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Equal(1, observation.BodyRan);
        A.CallTo(() => session.CommitTransactionAsync()).MustNotHaveHappened();
        A.CallTo(() => session.AbortTransactionAsync()).MustNotHaveHappened();
        Assert.True(accessor.HasSession);
        Assert.Same(session, accessor.Session);
    }

    [Fact]
    public async Task RequestScopedRepositoryInsideTheHandler_IsANewInstanceWithoutTheSession()
    {
        var accessor = new OctoSessionAccessor();
        var (provider, observation) = Build(accessor);
        accessor.Session = FakeSession();
        using var requestScope = provider.CreateScope();
        var requestRepository = requestScope.ServiceProvider.GetRequiredService<SessionBoundRepository>();

        var response = await provider.GetRequiredService<ICkInProcessMethodTransport>()
            .InvokeAsAuthorizedCallerAsync(Request(), TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.True(requestRepository.CreatedWithSession);
        Assert.NotNull(observation.Repository);
        Assert.NotSame(requestRepository, observation.Repository);
        Assert.False(observation.Repository.CreatedWithSession);
    }

    [Fact]
    public async Task Tripwire_WhenTheSessionWouldFlowIntoTheHandler_TheCallFailsBeforeTheHandlerBodyRuns()
    {
        var (provider, observation) = Build(new SessionFlowsIntoHandlerAccessor());
        var request = Request();

        var response = await provider.GetRequiredService<ICkInProcessMethodTransport>()
            .InvokeAsAuthorizedCallerAsync(request, TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.Equal(MethodId, response.Error.Parameters[CkMethodErrorParameters.MethodId]);
        Assert.Equal(0, observation.BodyRan);
        Assert.Equal(0, observation.HandlerConstructed); // refused at entry, the handler was not even created
    }

    [Fact]
    public async Task Tripwire_WithoutAnAccessor_FailsClosed()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(new Observation());
        services.AddScoped<SessionBoundRepository>();
        services.AddScoped<TouchHandler>();
        services.AddFakeMethodDefinition(MethodId);
        services.AddCkInProcessMethodHandlers(m => m.Handle<TouchHandler>(MethodId));
        services.AddOctoSessionAmbientStateProbe();
        using var provider = services.BuildServiceProvider();
        await provider.InitializeMethodsAsync();

        var response = await provider.GetRequiredService<ICkInProcessMethodTransport>()
            .InvokeAsAuthorizedCallerAsync(Request(), TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
    }

    [Fact]
    public async Task Probe_ReportsTheSessionOfTheAccessorAndNothingElse()
    {
        var accessor = new OctoSessionAccessor();
        var services = new ServiceCollection().AddSingleton<IOctoSessionAccessor>(accessor).BuildServiceProvider();
        var probe = new OctoSessionAmbientStateProbe();

        // The accessor is flow-local: run each state in its own flow so the test order cannot matter.
        var withoutSession = await Task.Run(() => probe.IsRequestStateVisible(services),
            TestContext.Current.CancellationToken);
        var withSession = await Task.Run(() =>
        {
            accessor.Session = FakeSession();
            return probe.IsRequestStateVisible(services);
        }, TestContext.Current.CancellationToken);

        Assert.False(withoutSession);
        Assert.True(withSession);
    }

    [Fact]
    public void AddCkMethodGateway_RegistersTheProbeExactlyOnce()
    {
        var services = new ServiceCollection();

        services.AddCkMethodGateway();
        services.AddCkMethodGateway();

        Assert.Single(services, d => d.ServiceType == typeof(ICkInProcessAmbientStateProbe) &&
                                     d.ImplementationType == typeof(OctoSessionAmbientStateProbe));
    }
}
