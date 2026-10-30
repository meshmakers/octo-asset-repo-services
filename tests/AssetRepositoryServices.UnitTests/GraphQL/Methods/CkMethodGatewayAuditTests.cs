using System.Security.Claims;
using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.AuditTrails;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Audit;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL.Methods;

/// <summary>
///     AB#7826 (F3.5): the gateway records every call it refuses before dispatch through <see cref="ICkMethodAuditRecorder" />
///     (transport <c>gateway</c>, the invocation id it minted, the refusal code, no handler instance), and a call that passes
///     the gateway is recorded by the band-100 audit filter only. The tests run the real recorder against a capturing sink, so
///     the scanned text is the event that the event log would store.
/// </summary>
public class CkMethodGatewayAuditTests
{
    private const string MethodId = "Sample.Accounts/Account.Touch-1";
    private const string CkType = "Sample.Accounts/Account";
    private const string Tenant = MethodCkCacheFake.Tenant;
    private const string Owner = "65f0c0ffee65f0c0ffee65f0";
    private const string Caller = "sub-audit-1";
    private const string CanaryValue = "CANARY-PARAMETER-VALUE-7826";
    private const string CanaryToken = "CANARY-TOKEN-7826";
    private const string CanaryKey = "CANARY-IDEMPOTENCY-KEY-7826";
    private const int MaxBytes = 2048;

    public const string Forbidden = "forbidden";
    public const string TooLarge = "too-large";
    public const string NotFound = "not-found";
    public const string DataPermission = "data-permission";
    public const string Invalid = "invalid";

    public static TheoryData<string, string> RefusalPaths => new()
    {
        { Forbidden, CkMethodErrorCodes.MethodForbidden },
        { TooLarge, CkMethodErrorCodes.MethodParametersTooLarge },
        { NotFound, CkMethodErrorCodes.MethodTargetNotFound },
        { DataPermission, CkMethodErrorCodes.MethodForbidden },
        { Invalid, CkMethodErrorCodes.MethodValidationFailed },
    };

    private sealed class CapturingSink : IAuditEventSink
    {
        public List<AuditEvent> Events { get; } = [];

        public Task PublishAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            lock (Events)
            {
                Events.Add(auditEvent);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRecorder : ICkMethodAuditRecorder
    {
        public List<CkMethodAuditRecord> Records { get; } = [];

        public Exception? Throw { get; set; }

        public Task RecordAsync(CkMethodAuditRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Throw == null ? Task.CompletedTask : Task.FromException(Throw);
        }
    }

    private sealed class Source : ICkMethodAuthorizationDefinitionSource
    {
        public CkMethodDto? Find(string tenantId, string ckTypeId, string qualifiedMethodId) =>
            qualifiedMethodId == MethodId
                ? new CkMethodDto
                {
                    MethodId = "Touch-1",
                    Parameters =
                    [
                        new CkMethodParameterDto { Name = "note", ValueType = AttributeValueTypesDto.String, IsOptional = true }
                    ],
                    Authorization = new CkMethodAuthorizationDto { Roles = [MethodCkCacheFake.DeclaredRole] }
                }
                : null;
    }

    private sealed class Dispatcher(Func<CkMethodRequest, Task<CkMethodResponse>> run) : ICkMethodDispatcher
    {
        public int Calls;

        public Task<CkMethodResponse> InvokeAsync(CkMethodRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return run(request);
        }
    }

    private static ClaimsPrincipal User(params string[] roles)
    {
        var claims = new List<Claim> { new("sub", Caller), new("scope", "octo_api openid"), new("name", "Audit Tester") };
        claims.AddRange(roles.Select(r => new Claim("role", r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static CkMethodGatewayCall Call(ClaimsPrincipal user, string parameters = "{}") => new(
        Tenant, CkType, MethodId, Owner, parameters, user, CanaryToken, IdempotencyKey: CanaryKey);

    private static CkMethodGateway Gateway(ICkMethodAuditRecorder recorder, ICkMethodDispatcher dispatcher) => new(
        new CkMethodAuthorizationEvaluator(Options.Create(new CkMethodAuthorizationOptions())), new Source(),
        Timeout(), dispatcher, recorder, NullLogger<CkMethodGateway>.Instance,
        Options.Create(new CkMethodLimitsOptions { MaxParametersBytes = MaxBytes }));

    private static ICkMethodTimeoutResolver Timeout()
    {
        var timeout = A.Fake<ICkMethodTimeoutResolver>();
        A.CallTo(() => timeout.GetDeadlineUtc(A<string>._, A<string>._, A<string>._)).Returns(DateTime.UtcNow.AddSeconds(15));
        return timeout;
    }

    /// <summary>Gives the gateway the caller and the target access that lead to the named refusal path.</summary>
    private static (CkMethodGatewayCall Call, CkMethodTargetAccessCheck Access) Scenario(string path)
    {
        var allowed = User(MethodCkCacheFake.DeclaredRole);
        CkMethodTargetAccessCheck ok = (_, _) => Task.FromResult(CkMethodTargetAccess.Allowed);
        return path switch
        {
            Forbidden => (Call(User("Nobody"), $"{{\"note\":\"{CanaryValue}\"}}"), ok),
            TooLarge => (Call(allowed, $"{{\"note\":\"{CanaryValue}{new string('x', MaxBytes)}\"}}"), ok),
            NotFound => (Call(allowed, $"{{\"note\":\"{CanaryValue}\"}}"),
                (_, _) => Task.FromResult(CkMethodTargetAccess.NotVisible)),
            DataPermission => (Call(allowed, $"{{\"note\":\"{CanaryValue}\"}}"),
                (_, _) => Task.FromResult(CkMethodTargetAccess.NotPermitted)),
            Invalid => (Call(allowed, $"{{\"note\":7,\"surprise\":\"{CanaryValue}\"}}"), ok),
            _ => throw new ArgumentOutOfRangeException(nameof(path))
        };
    }

    [Theory]
    [MemberData(nameof(RefusalPaths))]
    public async Task EveryRefusalBeforeDispatch_IsRecordedOnce_WithTransportGateway_AndTheInvocationIdTheCallerSees(
        string path, string expectedCode)
    {
        var recorder = new RecordingRecorder();
        var dispatcher = new Dispatcher(r => Task.FromResult(CkMethodResponse.Succeeded(r.InvocationId, "h", "{}")));
        var (call, access) = Scenario(path);

        var response = await Gateway(recorder, dispatcher).InvokeAsync(call, access, TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal(expectedCode, response.Error!.Code);
        Assert.Equal(0, dispatcher.Calls);
        var record = Assert.Single(recorder.Records);
        Assert.Equal(CkMethodAuditRecord.GatewayTransport, record.Transport);
        Assert.Equal("gateway", record.Transport);
        Assert.Equal(expectedCode, record.Outcome);
        Assert.Equal(response.InvocationId, record.InvocationId);
        Assert.Equal(Tenant, record.TenantId);
        Assert.Equal(MethodId, record.MethodId);
        Assert.Equal(CkType, record.CkTypeId);
        Assert.Equal(Owner, record.TargetRtId);
        Assert.Equal(Caller, record.CallerSubject);
        Assert.Equal("Audit Tester", record.CallerName);
        Assert.Null(record.HandlerInstanceId);
        Assert.Null(record.ReplayOf);
    }

    [Fact]
    public async Task ARefusedCallOfAnAnonymousCaller_IsRecordedWithoutASubject()
    {
        var recorder = new RecordingRecorder();
        var call = Call(new ClaimsPrincipal(new ClaimsIdentity()));

        var response = await Gateway(recorder, new Dispatcher(_ => throw new InvalidOperationException()))
            .InvokeAsync(call, (_, _) => Task.FromResult(CkMethodTargetAccess.Allowed), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
        var record = Assert.Single(recorder.Records);
        Assert.Null(record.CallerSubject);
        Assert.Equal("gateway", record.Transport);
    }

    [Theory]
    [MemberData(nameof(RefusalPaths))]
    public async Task TheRealRecorder_WritesOneEvent_ThatCarriesNeitherAParameterValueNorTheTokenNorTheKey(string path,
        string expectedCode)
    {
        var sink = new CapturingSink();
        using var provider = Provider(sink);
        var recorder = provider.GetRequiredService<ICkMethodAuditRecorder>();
        var (call, access) = Scenario(path);

        var response = await Gateway(recorder, new Dispatcher(_ => throw new InvalidOperationException()))
            .InvokeAsync(call, access, TestContext.Current.CancellationToken);

        Assert.Equal(expectedCode, response.Error!.Code);
        var auditEvent = Assert.Single(sink.Events);
        Assert.Equal(CkMethodAuditEvent.Category, auditEvent.Category);
        Assert.Equal("gateway", auditEvent.Metadata![CkMethodAuditEvent.Keys.Transport]);
        Assert.Equal(expectedCode, auditEvent.Metadata[CkMethodAuditEvent.Keys.Outcome]);
        Assert.Equal(response.InvocationId.ToString(), auditEvent.Metadata[CkMethodAuditEvent.Keys.InvocationId]);
        Assert.Null(auditEvent.Metadata[CkMethodAuditEvent.Keys.HandlerInstanceId]);
        AssertNoCanary(auditEvent);
    }

    [Fact]
    public async Task APassingCall_IsRecordedByTheFilterOnly_ExactlyOneEvent_WithTheTransportOfTheDispatch()
    {
        var sink = new CapturingSink();
        using var provider = Provider(sink, withPipeline: true);
        var dispatcher = new PipelineDispatcher(provider.GetRequiredService<ICkInProcessMethodTransport>());
        var recorder = provider.GetRequiredService<ICkMethodAuditRecorder>();
        var call = Call(User(MethodCkCacheFake.DeclaredRole), $"{{\"note\":\"{CanaryValue}\"}}");

        var response = await Gateway(recorder, dispatcher).InvokeAsync(call,
            (_, _) => Task.FromResult(CkMethodTargetAccess.Allowed), TestContext.Current.CancellationToken);

        Assert.True(response.Success, response.Error?.Code);
        Assert.Equal(1, dispatcher.Calls);
        var auditEvent = Assert.Single(sink.Events); // not one from the gateway and one from the filter
        Assert.Equal(CkMethodAuditOutcomes.Success, auditEvent.Metadata![CkMethodAuditEvent.Keys.Outcome]);
        Assert.Equal(CkMethodTransportNames.InProcess, auditEvent.Metadata[CkMethodAuditEvent.Keys.Transport]);
        Assert.Equal(response.InvocationId.ToString(), auditEvent.Metadata[CkMethodAuditEvent.Keys.InvocationId]);
        AssertNoCanary(auditEvent);
    }

    [Fact]
    public async Task ARefusedCallAndAPassingCall_AreOneEventEach_WithTheirOwnInvocationIds()
    {
        var sink = new CapturingSink();
        using var provider = Provider(sink, withPipeline: true);
        var gateway = Gateway(provider.GetRequiredService<ICkMethodAuditRecorder>(),
            new PipelineDispatcher(provider.GetRequiredService<ICkInProcessMethodTransport>()));
        CkMethodTargetAccessCheck ok = (_, _) => Task.FromResult(CkMethodTargetAccess.Allowed);

        var refused = await gateway.InvokeAsync(Call(User("Nobody")), ok, TestContext.Current.CancellationToken);
        var passed = await gateway.InvokeAsync(Call(User(MethodCkCacheFake.DeclaredRole)), ok,
            TestContext.Current.CancellationToken);

        Assert.False(refused.Success);
        Assert.True(passed.Success);
        Assert.Equal(2, sink.Events.Count);
        var ids = sink.Events.Select(e => (string)e.Metadata![CkMethodAuditEvent.Keys.InvocationId]!).ToList();
        Assert.Equal([refused.InvocationId.ToString(), passed.InvocationId.ToString()], ids);
        Assert.Equal(["gateway", CkMethodTransportNames.InProcess],
            sink.Events.Select(e => (string)e.Metadata![CkMethodAuditEvent.Keys.Transport]!).ToList());
    }

    [Fact]
    public async Task WhenTheAuditWriteFails_ARefusalStaysTheRefusal()
    {
        var recorder = new RecordingRecorder
        {
            Throw = new CkMethodAuditFailedException(Guid.NewGuid(), new IOException("store down"))
        };
        var (call, access) = Scenario(NotFound);

        var response = await Gateway(recorder, new Dispatcher(_ => throw new InvalidOperationException()))
            .InvokeAsync(call, access, TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodTargetNotFound, response.Error!.Code);
        Assert.Single(recorder.Records);
    }

    [Fact]
    public async Task ACustomRecorderThatThrows_DoesNotTurnARefusalIntoAFault()
    {
        var recorder = new RecordingRecorder { Throw = new InvalidOperationException("boom") };
        var (call, access) = Scenario(Forbidden);

        var response = await Gateway(recorder, new Dispatcher(_ => throw new InvalidOperationException()))
            .InvokeAsync(call, access, TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
    }

    [Fact]
    public async Task ACancelledCaller_StillGetsTheRefusalRecorded_WithATokenThatIsNotCancelled()
    {
        var recorder = A.Fake<ICkMethodAuditRecorder>();
        CancellationToken seen = new(true);
        A.CallTo(() => recorder.RecordAsync(A<CkMethodAuditRecord>._, A<CancellationToken>._))
            .Invokes((CkMethodAuditRecord _, CancellationToken t) => seen = t).Returns(Task.CompletedTask);
        var (call, access) = Scenario(Forbidden);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var response = await Gateway(recorder, new Dispatcher(_ => throw new InvalidOperationException()))
            .InvokeAsync(call, access, cts.Token);

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
        Assert.False(seen.IsCancellationRequested);
    }

    private static void AssertNoCanary(AuditEvent auditEvent)
    {
        var text = auditEvent.Message + "\n" + JsonSerializer.Serialize(auditEvent.Metadata);
        Assert.DoesNotContain(CanaryValue, text);
        Assert.DoesNotContain(CanaryToken, text);
        Assert.DoesNotContain(CanaryKey, text);
        Assert.DoesNotContain("surprise", text);
    }

    private sealed class PipelineDispatcher(ICkInProcessMethodTransport transport) : ICkMethodDispatcher
    {
        public int Calls;

        public Task<CkMethodResponse> InvokeAsync(CkMethodRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return transport.InvokeAsync(request, cancellationToken);
        }
    }

    public sealed record TouchParameters
    {
        public string? Note { get; init; }
    }

    public sealed record TouchResult
    {
        public string? Value { get; init; }
    }

    private sealed class TouchHandler : ICkMethodHandler<TouchParameters, TouchResult>
    {
        public Task<CkMethodOutcome<TouchResult>> HandleAsync(CkMethodContext<TouchParameters> context,
            CancellationToken cancellationToken) =>
            Task.FromResult<CkMethodOutcome<TouchResult>>(new TouchResult { Value = "done" });
    }

    private static ServiceProvider Provider(IAuditEventSink sink, bool withPipeline = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(sink);
        services.AddCkMethodAudit();
        if (withPipeline)
        {
            services.AddFakeMethodDefinition(MethodId);
            services.AddScoped<TouchHandler>();
            services.AddCkInProcessMethodHandlers(m => m.Handle<TouchHandler>(MethodId));
        }

        var provider = services.BuildServiceProvider();
        if (withPipeline)
        {
            provider.InitializeMethodsAsync().GetAwaiter().GetResult();
        }

        return provider;
    }
}
