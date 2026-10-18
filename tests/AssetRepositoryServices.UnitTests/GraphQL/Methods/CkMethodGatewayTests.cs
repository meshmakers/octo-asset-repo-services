using System.Security.Claims;
using System.Text.Json;
using FakeItEasy;
using GraphQL;
using GraphQL.SystemTextJson;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL.Methods;

/// <summary>
///     AB#6912: the gateway authorizes with the shared evaluator BEFORE it loads the target. A refusal is the same
///     <c>METHOD_FORBIDDEN</c> for an existing and a missing target (response, GraphQL error and logs), a counting
///     target reader proves that an unauthorized call reads nothing, and <c>METHOD_TARGET_NOT_FOUND</c> is only for a
///     caller who passed.
/// </summary>
public class CkMethodGatewayTests
{
    private const string MethodId = "Sample.Accounts/Account.ChangePassword-1";
    private const string Tenant = "meshtest";
    private const string Owner = "65f0c0ffee65f0c0ffee65f0";
    private const string OwnerUpper = "65F0C0FFEE65F0C0FFEE65F0";
    private const string Other = "65f0c0ffee65f0c0ffee65f1";
    private const string Missing = "000000000000000000000000";

    private readonly ICkMethodDispatcher _dispatcher = A.Fake<ICkMethodDispatcher>();
    private readonly List<string> _logs = [];
    private readonly List<CkMethodRequest> _dispatched = [];
    private CkMethodPrincipal? _principalSeenByDispatcher;

    public CkMethodGatewayTests()
    {
        A.CallTo(() => _dispatcher.InvokeAsync(A<CkMethodRequest>._, A<CancellationToken>._))
            .ReturnsLazily((CkMethodRequest r, CancellationToken _) =>
            {
                _dispatched.Add(r);
                _principalSeenByDispatcher = CkMethodPrincipalScope.CurrentPrincipal;
                return Task.FromResult(CkMethodResponse.Succeeded(r.InvocationId, "handler-1", "{\"value\":1}"));
            });
    }

    private sealed class Counter
    {
        public int Reads;

        public bool Exists = true;

        public Task<bool> ExistsAsync(CancellationToken _)
        {
            Interlocked.Increment(ref Reads);
            return Task.FromResult(Exists);
        }
    }

    private sealed class Source(CkMethodDto? definition) : ICkMethodAuthorizationDefinitionSource
    {
        public CkMethodDto? Find(string tenantId, string ckTypeId, string qualifiedMethodId) =>
            qualifiedMethodId == MethodId ? definition : null;
    }

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
                    sink.Add($"{logLevel} {formatter(state, exception)} {exception}");
                }
            }
        }
    }

    private static CkMethodDto Definition(CkMethodAuthorizationDto? authorization) => new()
    {
        MethodId = "ChangePassword-1",
        Parameters = [new CkMethodParameterDto { Name = "newPassword", ValueType = AttributeValueTypesDto.String, Sensitive = true }],
        Authorization = authorization
    };

    private ICkMethodGateway Gateway(CkMethodAuthorizationDto? authorization)
    {
        var timeout = A.Fake<ICkMethodTimeoutResolver>();
        A.CallTo(() => timeout.GetDeadlineUtc(A<string>._, A<string>._, A<string>._))
            .Returns(DateTime.UtcNow.AddSeconds(15));
        var loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(new ListLoggerProvider(_logs)));
        return new CkMethodGateway(
            new CkMethodAuthorizationEvaluator(Options.Create(new CkMethodAuthorizationOptions())),
            new Source(Definition(authorization)), timeout, _dispatcher, loggers.CreateLogger<CkMethodGateway>());
    }

    private static ClaimsPrincipal User(string sub, string[] roles, string scope = "octo_api openid")
    {
        var claims = new List<Claim> { new("sub", sub), new("scope", scope), new("name", "Test User") };
        claims.AddRange(roles.Select(r => new Claim("role", r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static CkMethodGatewayCall Call(ClaimsPrincipal user, string target = Owner) => new(
        Tenant, "Sample.Accounts/Account", MethodId, target, "{\"newPassword\":\"CANARY-SECRET-123\"}", user, "CANARY-TOKEN");

    private static string GraphQl(CkMethodResponse response) => new GraphQLSerializer().Serialize(
        new ExecutionResult { Errors = new ExecutionErrors { CkMethodGraphQlError.Create(response.Error!) } });

    [Fact]
    public async Task Refusal_IsByteIdentical_ForAnExistingAndAMissingTarget_AndReadsNothing()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        var reader = new Counter();
        var user = User(Other, ["Nobody"]);

        var existing = await gateway.InvokeAsync(Call(user, Owner), reader.ExistsAsync, TestContext.Current.CancellationToken);
        reader.Exists = false;
        var missing = await gateway.InvokeAsync(Call(user, Missing), reader.ExistsAsync, TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, existing.Error!.Code);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, missing.Error!.Code);
        // the invocation id is the only part of the envelope that differs; the error and the GraphQL error are identical
        Assert.Equal(ErrorJson(existing), ErrorJson(missing));
        Assert.Equal(GraphQl(existing), GraphQl(missing));
        Assert.Equal(0, reader.Reads);
        Assert.Empty(_dispatched);
    }

    private static string ErrorJson(CkMethodResponse response) => JsonSerializer.Serialize(response.Error);

    [Fact]
    public async Task Refusal_LogsAtInformationLevel_WithoutNamingTheEntityTheParametersOrTheToken()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });

        await gateway.InvokeAsync(Call(User(Other, ["Nobody"]), Owner), new Counter().ExistsAsync, TestContext.Current.CancellationToken);
        await gateway.InvokeAsync(Call(User(Other, ["Nobody"]), Missing), new Counter().ExistsAsync, TestContext.Current.CancellationToken);

        Assert.NotEmpty(_logs);
        var all = string.Join("\n", _logs);
        Assert.DoesNotContain(Owner, all);
        Assert.DoesNotContain(Missing, all);
        Assert.DoesNotContain("CANARY-SECRET-123", all);
        Assert.DoesNotContain("CANARY-TOKEN", all);
        Assert.Contains(MethodId, all);
    }

    [Fact]
    public async Task AuthorizedCaller_MissingTarget_GetsTargetNotFound_AndNothingIsDispatched()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        var reader = new Counter { Exists = false };

        var response = await gateway.InvokeAsync(Call(User(Other, ["AssetManagement"]), Missing), reader.ExistsAsync,
            TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodTargetNotFound, response.Error!.Code);
        Assert.Equal(MethodId, response.Error.Parameters[CkMethodErrorParameters.MethodId]);
        Assert.Equal(1, reader.Reads);
        Assert.Empty(_dispatched);
    }

    [Fact]
    public async Task AuthorizedCaller_ExistingTarget_IsDispatched_WithTheRawValuesAndThePrincipalInScope()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        var reader = new Counter();

        var response = await gateway.InvokeAsync(Call(User(Other, ["assetmanagement"]), Owner), reader.ExistsAsync,
            TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Equal(1, reader.Reads);
        var request = Assert.Single(_dispatched);
        Assert.Equal(MethodId, request.MethodId);
        Assert.Equal(Owner, request.TargetRtId);
        Assert.Equal("CANARY-TOKEN", request.CallerAccessToken);
        Assert.Contains("CANARY-SECRET-123", request.ParametersJson);
        Assert.Equal(Other, request.Caller!.SubjectId);
        Assert.Equal(Other, _principalSeenByDispatcher!.SubjectId);
        Assert.Null(CkMethodPrincipalScope.CurrentPrincipal); // the scope ends with the call
    }

    [Fact]
    public async Task AllowSelf_PassesAcrossLetterCase_AnotherCallerIsRefusedBeforeAnyRead()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { AllowSelf = true });
        var reader = new Counter();

        var self = await gateway.InvokeAsync(Call(User(OwnerUpper, []), Owner), reader.ExistsAsync, TestContext.Current.CancellationToken);
        var reads = reader.Reads;
        var stranger = await gateway.InvokeAsync(Call(User(Other, []), Owner), reader.ExistsAsync, TestContext.Current.CancellationToken);

        Assert.True(self.Success, self.Error?.Code);
        Assert.Equal(1, reads);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, stranger.Error!.Code);
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task MethodCall_WithoutTheOctoApiScope_IsRefused_NoMatterWhatTheMutationScopeFlagSays()
    {
        // the gateway never reads GraphQl:EnforceMutationScope; octo_api is part of every method authorization
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        var reader = new Counter();

        var response = await gateway.InvokeAsync(Call(User(Other, ["AssetManagement"], scope: "openid profile"), Owner),
            reader.ExistsAsync, TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
        Assert.Equal(0, reader.Reads);
        Assert.Empty(_dispatched);
    }

    [Fact]
    public async Task UnauthenticatedCaller_AndUnknownMethod_AreRefusedTheSameWay()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        var reader = new Counter();

        var anonymous = await gateway.InvokeAsync(Call(new ClaimsPrincipal(new ClaimsIdentity()), Owner), reader.ExistsAsync,
            TestContext.Current.CancellationToken);
        var unknown = await gateway.InvokeAsync(
            Call(User(Other, ["AssetManagement"])) with { MethodId = "Sample.Accounts/Account.Nope-1" },
            reader.ExistsAsync, TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, anonymous.Error!.Code);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, unknown.Error!.Code);
        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task DefaultDeny_NoDeclaredAuthorization_OnlyAnAdministratorPasses()
    {
        var gateway = Gateway(authorization: null);
        var reader = new Counter();

        var owner = await gateway.InvokeAsync(Call(User(Owner, []), Owner), reader.ExistsAsync, TestContext.Current.CancellationToken);
        var admin = await gateway.InvokeAsync(Call(User(Other, ["AdminPanelManagement"]), Owner), reader.ExistsAsync,
            TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, owner.Error!.Code);
        Assert.True(admin.Success);
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task StaticMethod_NeverReadsATarget()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["R"] });
        var reader = new Counter();

        var response = await gateway.InvokeAsync(Call(User(Other, ["R"]), target: null!) with { TargetRtId = null },
            reader.ExistsAsync, TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public void GraphQlError_CarriesCodeAndParametersInExtensions_AndNoStackOrData()
    {
        var error = CkMethodAuthorizationErrors.Forbidden(MethodId);

        var json = JsonDocument.Parse(new GraphQLSerializer().Serialize(
            new ExecutionResult { Errors = new ExecutionErrors { CkMethodGraphQlError.Create(error) } }));

        var first = json.RootElement.GetProperty("errors")[0];
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, first.GetProperty("extensions").GetProperty("code").GetString());
        var parameters = first.GetProperty("extensions").GetProperty("parameters");
        Assert.Equal(MethodId, parameters.GetProperty(CkMethodErrorParameters.MethodId).GetString());
        Assert.Equal("not-authorized", parameters.GetProperty(CkMethodErrorParameters.Reason).GetString());
    }
}
