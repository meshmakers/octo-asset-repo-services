using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FakeItEasy;
using GraphQL;
using GraphQL.SystemTextJson;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AssetRepositoryServices.UnitTests.Adversarial;

/// <summary>
///     AB#6915, gateway layer: attacks on <see cref="ICkMethodGateway" /> with the real evaluator and the real parameter
///     validator, a counting target access check and a dispatcher double. Every refused case asserts the documented
///     code, that the target was never read and that nothing was dispatched (no side effect).
/// </summary>
public class CkMethodGatewayAdversarialTests
{
    private const string MethodId = "Sample.Accounts/Account.ChangePassword-1";
    private const string Tenant = "meshtest";
    private const string Owner = "65f0c0ffee65f0c0ffee65f0";
    private const string Other = "65f0c0ffee65f0c0ffee65f1";
    private const string OtherUpper = "65F0C0FFEE65F0C0FFEE65F1";
    private const string Missing = "000000000000000000000000";
    private const string Canary = "CANARY-SECRET-6915";
    private const string TokenCanary = "CANARY-TOKEN-6915";

    private readonly ICkMethodDispatcher _dispatcher = A.Fake<ICkMethodDispatcher>();
    private readonly List<string> _logs = [];
    private readonly List<CkMethodRequest> _dispatched = [];
    private readonly Counter _target = new();

    public CkMethodGatewayAdversarialTests()
    {
        A.CallTo(() => _dispatcher.InvokeAsync(A<CkMethodRequest>._, A<CancellationToken>._))
            .ReturnsLazily((CkMethodRequest r, CancellationToken _) =>
            {
                _dispatched.Add(r);
                return Task.FromResult(CkMethodResponse.Succeeded(r.InvocationId, "handler-1", "{\"value\":1}"));
            });
    }

    private sealed class Counter
    {
        public int Reads;

        public Task<CkMethodTargetAccess> CheckAsync(CkMethodDataAccessDto dataAccess, CancellationToken _)
        {
            Interlocked.Increment(ref Reads);
            return Task.FromResult(CkMethodTargetAccess.Allowed);
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
        Parameters =
        [
            new CkMethodParameterDto { Name = "newPassword", ValueType = AttributeValueTypesDto.String, Sensitive = true },
            new CkMethodParameterDto { Name = "note", ValueType = AttributeValueTypesDto.String, IsOptional = true }
        ],
        Authorization = authorization
    };

    private ICkMethodGateway Gateway(CkMethodAuthorizationDto? authorization, bool knownMethod = true)
    {
        var timeout = A.Fake<ICkMethodTimeoutResolver>();
        A.CallTo(() => timeout.GetDeadlineUtc(A<string>._, A<string>._, A<string>._))
            .Returns(DateTime.UtcNow.AddSeconds(15));
        var loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(new ListLoggerProvider(_logs)));
        return new CkMethodGateway(
            new CkMethodAuthorizationEvaluator(Options.Create(new CkMethodAuthorizationOptions())),
            new Source(knownMethod ? Definition(authorization) : null), timeout, _dispatcher,
            loggers.CreateLogger<CkMethodGateway>());
    }

    private static ClaimsPrincipal User(string sub, string[] roles, string scope = "octo_api openid", string? tenant = null)
    {
        var claims = new List<Claim> { new("sub", sub), new("scope", scope) };
        if (tenant != null)
        {
            claims.Add(new Claim("tenant_id", tenant));
        }

        claims.AddRange(roles.Select(r => new Claim("role", r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static CkMethodGatewayCall Call(ClaimsPrincipal user, string? target = Owner, string? parameters = null) => new(
        Tenant, "Sample.Accounts/Account", MethodId, target,
        parameters ?? "{\"newPassword\":\"" + Canary + "\"}", user, TokenCanary);

    private static string Observed(CkMethodResponse response) =>
        response.Success
            ? "success"
            : response.Error!.Code + (response.Error.Parameters.TryGetValue(CkMethodErrorParameters.Reason, out var reason)
                ? $" / {reason}"
                : string.Empty);

    private void Record(string caseId, string attack, CkMethodResponse response, [CallerMemberName] string test = "") =>
        AdversarialCaseLog.Record(caseId, attack, "CkMethodGatewayAdversarialTests." + test, Observed(response),
            _dispatched.Count == 0 && _target.Reads == 0
                ? "none (target not read, nothing dispatched)"
                : $"target reads {_target.Reads}, dispatched {_dispatched.Count}");

    private Task<CkMethodResponse> InvokeAsync(ICkMethodGateway gateway, CkMethodGatewayCall call) =>
        gateway.InvokeAsync(call, _target.CheckAsync, TestContext.Current.CancellationToken);

    private static string GraphQl(CkMethodResponse response) => new GraphQLSerializer().Serialize(
        new ExecutionResult { Errors = new ExecutionErrors { CkMethodGraphQlError.Create(response.Error!) } });

    [Fact]
    public async Task C01_UnauthenticatedCaller_IsForbidden_BeforeAnythingIsRead()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"], AllowSelf = true });

        var response = await InvokeAsync(gateway, Call(new ClaimsPrincipal(new ClaimsIdentity())));

        Record("C01", "no authenticated principal at the gateway", response);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
        Assert.Equal(0, _target.Reads);
        Assert.Empty(_dispatched);
    }

    [Fact]
    public async Task C01b_TheCallerOfTheDispatchedRequest_IsBuiltFromTheValidatedClaimsOnly()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        // claims a client may try to smuggle in next to the real ones are not role claims and change nothing
        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", Other), new Claim("scope", "octo_api"), new Claim("role", "AssetManagement"),
            new Claim("caller", "{\"roles\":[\"AdminPanelManagement\"]}"), new Claim("roles_override", "AdminPanelManagement")
        ], "test"));

        var response = await InvokeAsync(gateway, Call(user));

        Record("C01b", "forged caller / roles_override claims next to the real claims", response);
        Assert.True(response.Success);
        var request = Assert.Single(_dispatched);
        Assert.Equal(Other, request.Caller!.SubjectId);
        Assert.Equal(["AssetManagement"], request.Caller.Roles);
    }

    [Fact]
    public async Task C02c_Limitation_TheGatewayDoesNotCompareTheTokenTenantWithTheRouteTenant_TheHttpGateDoes()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        var user = User(Other, ["AssetManagement"], tenant: "othertenant");

        var response = await InvokeAsync(gateway, Call(user));

        Record("C02c", "LIMITATION L-3: token tenant 'othertenant', call for tenant 'meshtest' at the gateway (the HTTP tenant gate in mode Enforce is the barrier, see BearerTokenAttackTests C02/C02b)", response);
        Assert.True(response.Success);
        var request = Assert.Single(_dispatched);
        Assert.Equal(Tenant, request.TenantId);
        Assert.Equal("othertenant", request.Caller!.TenantId);
    }

    [Fact]
    public async Task C07_TokenWithoutOctoApi_IsForbidden_EvenForAnAdministratorWithTheRole()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });

        var noScope = await InvokeAsync(gateway, Call(User(Other, ["AssetManagement", "AdminPanelManagement"], scope: "openid profile")));
        var otherScope = await InvokeAsync(gateway, Call(User(Other, ["AssetManagement"], scope: "octo_apiX")));

        Record("C07", "no octo_api scope, role and administrator present", noScope);
        Record("C07", "scope 'octo_apiX' (prefix match must not count)", otherScope);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, noScope.Error!.Code);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, otherScope.Error!.Code);
        Assert.Equal(0, _target.Reads);
        Assert.Empty(_dispatched);
    }

    [Fact]
    public async Task C08_AllowSelf_WithAnotherUsersId_IsForbidden_AlsoInAnotherLetterCase()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { AllowSelf = true });

        var lower = await InvokeAsync(gateway, Call(User(Other, []), Owner));
        var upper = await InvokeAsync(gateway, Call(User(OtherUpper, []), Owner));
        var unparsable = await InvokeAsync(gateway, Call(User("not-an-object-id", []), "not-an-object-id"));

        Record("C08", "allowSelf, another user's id", lower);
        Record("C08", "allowSelf, another user's id in upper case", upper);
        Record("C08", "allowSelf, caller and target 'not-an-object-id' (equal strings, unparsable)", unparsable);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, lower.Error!.Code);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, upper.Error!.Code);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, unparsable.Error!.Code);
        Assert.Equal(0, _target.Reads);
        Assert.Empty(_dispatched);

        // control: the owner in upper case passes (parsed ids)
        var self = await InvokeAsync(gateway, Call(User(Owner.ToUpperInvariant(), []), Owner));
        Assert.True(self.Success);
    }

    [Fact]
    public async Task C09_MethodWithoutAuthorization_CalledByItsOwner_IsForbidden()
    {
        var gateway = Gateway(authorization: null);

        var owner = await InvokeAsync(gateway, Call(User(Owner, []), Owner));
        var roleLookalike = await InvokeAsync(gateway, Call(User(Other, ["AdminPanelManagementX", "adminpanel"]), Owner));

        Record("C09", "no authorization declared, caller is the target's owner", owner);
        Record("C09", "no authorization declared, roles that only look like the administrator role", roleLookalike);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, owner.Error!.Code);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, roleLookalike.Error!.Code);
        Assert.Equal(0, _target.Reads);
        Assert.Empty(_dispatched);

        var admin = await InvokeAsync(gateway, Call(User(Other, ["adminpanelmanagement"]), Owner));
        Assert.True(admin.Success); // control: the configured administrator role, any case
    }

    [Fact]
    public async Task C10_Refusal_IsByteIdentical_ForExistingMissingAndUnknownMethodTargets()
    {
        var declared = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        var denyByDefault = Gateway(authorization: null);
        var unknownMethod = Gateway(null, knownMethod: false);
        var stranger = User(Other, ["Reporting"]);

        var existing = await InvokeAsync(declared, Call(stranger, Owner));
        var missing = await InvokeAsync(declared, Call(stranger, Missing));
        var defaultDenyExisting = await InvokeAsync(denyByDefault, Call(stranger, Owner));
        var defaultDenyMissing = await InvokeAsync(denyByDefault, Call(stranger, Missing));
        var unknown = await InvokeAsync(unknownMethod, Call(stranger, Owner));

        Record("C10", "caller without the role, existing target", existing);
        Record("C10", "caller without the role, missing target", missing);
        Record("C10", "default-deny, existing target", defaultDenyExisting);
        Record("C10", "default-deny, missing target", defaultDenyMissing);
        Record("C10", "method unknown to the CK cache", unknown);
        var expected = JsonSerializer.Serialize(existing.Error);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, existing.Error!.Code);
        Assert.Equal(expected, JsonSerializer.Serialize(missing.Error));
        Assert.Equal(expected, JsonSerializer.Serialize(defaultDenyExisting.Error));
        Assert.Equal(expected, JsonSerializer.Serialize(defaultDenyMissing.Error));
        Assert.Equal(expected, JsonSerializer.Serialize(unknown.Error)); // an attacker cannot tell a hidden method from a missing one
        Assert.Equal(GraphQl(existing), GraphQl(missing));
        Assert.Equal(0, _target.Reads);
        Assert.Empty(_dispatched);
    }

    private const int HugeNoteBytes = 8 * 1024 * 1024;

    /// <summary>
    ///     F-2 (AB#6915, fixed by AB#7665): parameters above the limit are refused with
    ///     <c>METHOD_PARAMETERS_TOO_LARGE</c> before the validator runs; nothing is dispatched.
    /// </summary>
    [Fact]
    public async Task C12_ParametersFarAboveTheLimit_AreRefused_AndNotDispatched()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        var parameters = "{\"newPassword\":\"x\",\"note\":\"" + new string('n', HugeNoteBytes) + "\"}";

        var response = await InvokeAsync(gateway, Call(User(Other, ["AssetManagement"]), Owner, parameters));

        Record("C12", "parameter payload of 8 MiB", response);
        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodParametersTooLarge, response.Error!.Code);
        Assert.Equal("1048576", response.Error.Parameters[CkMethodErrorParameters.MaxBytes]);
        Assert.Equal(Encoding.UTF8.GetByteCount(parameters).ToString(), response.Error.Parameters[CkMethodErrorParameters.ActualBytes]);
        Assert.Equal(0, _target.Reads);
        Assert.Empty(_dispatched);
    }

    [Fact]
    public async Task C12_ExactlyTheLimitPasses_OneByteMoreIsRefused_BeforeValidation()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        var user = User(Other, ["AssetManagement"]);
        var overhead = Encoding.UTF8.GetByteCount("{\"newPassword\":\"x\",\"note\":\"\"}");
        string Json(int bytes) => "{\"newPassword\":\"x\",\"note\":\"" + new string('n', bytes - overhead) + "\"}";

        var exact = await InvokeAsync(gateway, Call(user, Owner, Json(1024 * 1024)));
        var over = await InvokeAsync(gateway, Call(user, Owner, Json(1024 * 1024 + 1)));

        Assert.True(exact.Success, exact.Error?.Code);
        Assert.Equal(CkMethodErrorCodes.MethodParametersTooLarge, over.Error!.Code);
        Assert.Single(_dispatched);
    }

    [Fact]
    public async Task C12_OversizedAndMalformed_IsTooLarge_NotAValidationFailure_AndRefusedCallersLearnNothing()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });

        // malformed JSON above the limit: the size check comes first
        var malformed = await InvokeAsync(gateway, Call(User(Other, ["AssetManagement"]), Owner, "{" + new string('x', HugeNoteBytes)));
        // a caller without the role gets the authorization refusal, not the size answer
        var forbidden = await InvokeAsync(gateway, Call(User(Other, ["Reporting"]), Owner, "{" + new string('x', HugeNoteBytes)));

        Assert.Equal(CkMethodErrorCodes.MethodParametersTooLarge, malformed.Error!.Code);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, forbidden.Error!.Code);
        Assert.Empty(_dispatched);
    }

    [Fact]
    public async Task C12b_MalformedAndNonObjectParameters_AreValidationFailures_WithoutTheValue()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        var user = User(Other, ["AssetManagement"]);

        var truncated = await InvokeAsync(gateway, Call(user, Owner, "{\"newPassword\":\"" + Canary));
        var array = await InvokeAsync(gateway, Call(user, Owner, "[\"" + Canary + "\"]"));
        var nested = await InvokeAsync(gateway, Call(user, Owner, "{\"newPassword\":{\"a\":\"" + Canary + "\"}}"));
        var deep = await InvokeAsync(gateway, Call(user, Owner, new string('[', 5000) + "\"" + Canary + "\"" + new string(']', 5000)));

        Record("C12b", "truncated JSON carrying the canary", truncated);
        Record("C12b", "array instead of object", array);
        Record("C12b", "object where a string is declared", nested);
        Record("C12b", "5000 nested arrays", deep);
        foreach (var response in new[] { truncated, array, nested, deep })
        {
            Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, response.Error!.Code);
            Assert.DoesNotContain(Canary, JsonSerializer.Serialize(response));
        }

        Assert.Empty(_dispatched);
    }

    /// <summary>
    ///     F-3 (AB#6915, fixed by AB#7665): a code a responder wrote that is neither a platform code nor declared by the
    ///     method becomes <c>METHOD_HANDLER_FAILED</c> with the correlation id; the original code is logged, never returned.
    /// </summary>
    [Theory]
    [InlineData("METHOD_BOGUS_CODE")]
    [InlineData("UNDECLARED_CODE")]
    public void C13_AResponderCode_ThatIsNeitherPlatformNorDeclared_IsMappedToHandlerFailed(string code)
    {
        var invocation = Guid.NewGuid();
        var response = CkMethodResponse.Failed(invocation, "rogue",
            CkMethodError.Create(code, (CkMethodErrorParameters.MethodId, MethodId)));
        var method = new CkMethodGraph(new CkId<CkTypeId>("Sample.Accounts-1.0.0/Account-1"),
            new CkMethodDto { MethodId = "ChangePassword-1", Kind = CkMethodKindDto.Instance, Errors = [new CkMethodErrorDto { Code = "DECLARED_CODE" }] });
        var logs = new List<string>();
        var logger = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(new ListLoggerProvider(logs)))
            .CreateLogger("test");

        var outcome = CkMethodResponseMapper.Map(response, method, A.Fake<ICkCacheService>(), Tenant, logger);

        AdversarialCaseLog.Record("C13", $"responder answers with code {code}",
            "CkMethodGatewayAdversarialTests." + nameof(C13_AResponderCode_ThatIsNeitherPlatformNorDeclared_IsMappedToHandlerFailed),
            outcome.Error!.Code ?? "-", "none (GraphQL error only)");
        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, outcome.Error.Code);
        Assert.Equal(invocation.ToString("D"),
            ((IDictionary<string, object?>)outcome.Error.Extensions!["parameters"]!)[CkMethodErrorParameters.CorrelationId]);
        Assert.DoesNotContain(code, JsonSerializer.Serialize(outcome.Error.Extensions));
        Assert.Contains(logs, l => l.Contains(code));
    }

    [Theory]
    [InlineData(CkMethodErrorCodes.MethodForbidden)]
    [InlineData(CkMethodErrorCodes.MethodParametersTooLarge)]
    [InlineData("DECLARED_CODE")]
    public void C13_CatalogCodes_AndDeclaredCodes_PassUnchanged(string code)
    {
        var response = CkMethodResponse.Failed(Guid.NewGuid(), "h1",
            CkMethodError.Create(code, (CkMethodErrorParameters.MethodId, MethodId)));
        var method = new CkMethodGraph(new CkId<CkTypeId>("Sample.Accounts-1.0.0/Account-1"),
            new CkMethodDto { MethodId = "ChangePassword-1", Kind = CkMethodKindDto.Instance, Errors = [new CkMethodErrorDto { Code = "DECLARED_CODE" }] });

        var outcome = CkMethodResponseMapper.Map(response, method, A.Fake<ICkCacheService>(), Tenant);

        Assert.Equal(code, outcome.Error!.Code);
    }

    [Fact]
    public async Task C14_Canary_NeverAppearsInLogsOrErrors_OfAnyRefusedOrFailedCall()
    {
        var declared = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"], AllowSelf = true });
        var denyByDefault = Gateway(authorization: null);
        var responses = new List<CkMethodResponse>
        {
            await InvokeAsync(declared, Call(new ClaimsPrincipal(new ClaimsIdentity()))),
            await InvokeAsync(declared, Call(User(Other, ["Reporting"]))),
            await InvokeAsync(declared, Call(User(OtherUpper, []), Owner)),
            await InvokeAsync(denyByDefault, Call(User(Owner, []), Owner)),
            await InvokeAsync(declared, Call(User(Other, ["AssetManagement"], scope: "openid"))),
            await InvokeAsync(declared, Call(User(Other, ["AssetManagement"]), Owner, "{\"newPassword\":\"" + Canary)),
            await InvokeAsync(declared, Call(User(Other, ["AssetManagement"]), Owner, "{\"newPassword\":{\"x\":\"" + Canary + "\"}}"))
        };

        var text = string.Join("\n", _logs) + "\n" + string.Join("\n", responses.Select(r => JsonSerializer.Serialize(r))) + "\n" +
                   string.Join("\n", responses.Select(GraphQl));

        AdversarialCaseLog.Record("C14", $"canary (parameter, token) over {responses.Count} refused or failed calls: {_logs.Count} log lines, responses, GraphQL errors",
            "CkMethodGatewayAdversarialTests." + nameof(C14_Canary_NeverAppearsInLogsOrErrors_OfAnyRefusedOrFailedCall),
            text.Contains(Canary) || text.Contains(TokenCanary) ? "LEAK" : "no plaintext found", "none");
        Assert.NotEmpty(_logs);
        Assert.DoesNotContain(Canary, text);
        Assert.DoesNotContain(TokenCanary, text);
        Assert.Empty(_dispatched);
    }
}
