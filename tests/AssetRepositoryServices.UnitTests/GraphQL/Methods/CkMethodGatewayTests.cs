using System.Security.Claims;
using System.Text.Json;
using FakeItEasy;
using GraphQL;
using GraphQL.SystemTextJson;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
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

        public CkMethodTargetAccess? Verdict;

        public List<CkMethodDataAccessDto> Actions { get; } = [];

        public Exception? Throw;

        public Task<CkMethodTargetAccess> CheckAsync(CkMethodDataAccessDto dataAccess, CancellationToken _)
        {
            Interlocked.Increment(ref Reads);
            Actions.Add(dataAccess);
            if (Throw != null)
            {
                throw Throw;
            }

            return Task.FromResult(Verdict ?? (Exists ? CkMethodTargetAccess.Allowed : CkMethodTargetAccess.NotVisible));
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

        var existing = await gateway.InvokeAsync(Call(user, Owner), reader.CheckAsync, TestContext.Current.CancellationToken);
        reader.Exists = false;
        var missing = await gateway.InvokeAsync(Call(user, Missing), reader.CheckAsync, TestContext.Current.CancellationToken);

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

        await gateway.InvokeAsync(Call(User(Other, ["Nobody"]), Owner), new Counter().CheckAsync, TestContext.Current.CancellationToken);
        await gateway.InvokeAsync(Call(User(Other, ["Nobody"]), Missing), new Counter().CheckAsync, TestContext.Current.CancellationToken);

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

        var response = await gateway.InvokeAsync(Call(User(Other, ["AssetManagement"]), Missing), reader.CheckAsync,
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

        var response = await gateway.InvokeAsync(Call(User(Other, ["assetmanagement"]), Owner), reader.CheckAsync,
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
    public async Task IdempotencyKey_IsForwardedIntoTheRequest_AndAbsentWhenTheCallHasNone()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        var reader = new Counter();
        var user = User(Other, ["assetmanagement"]);

        await gateway.InvokeAsync(Call(user, Owner) with { IdempotencyKey = "key-7825" }, reader.CheckAsync,
            TestContext.Current.CancellationToken);
        await gateway.InvokeAsync(Call(user, Owner), reader.CheckAsync, TestContext.Current.CancellationToken);

        Assert.Equal(2, _dispatched.Count);
        Assert.Equal("key-7825", _dispatched[0].IdempotencyKey);
        Assert.Null(_dispatched[1].IdempotencyKey);
    }

    [Fact]
    public async Task AllowSelf_PassesAcrossLetterCase_AnotherCallerIsRefusedBeforeAnyRead()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { AllowSelf = true });
        var reader = new Counter();

        var self = await gateway.InvokeAsync(Call(User(OwnerUpper, []), Owner), reader.CheckAsync, TestContext.Current.CancellationToken);
        var reads = reader.Reads;
        var stranger = await gateway.InvokeAsync(Call(User(Other, []), Owner), reader.CheckAsync, TestContext.Current.CancellationToken);

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
            reader.CheckAsync, TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
        Assert.Equal(0, reader.Reads);
        Assert.Empty(_dispatched);
    }

    [Fact]
    public async Task UnauthenticatedCaller_AndUnknownMethod_AreRefusedTheSameWay()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        var reader = new Counter();

        var anonymous = await gateway.InvokeAsync(Call(new ClaimsPrincipal(new ClaimsIdentity()), Owner), reader.CheckAsync,
            TestContext.Current.CancellationToken);
        var unknown = await gateway.InvokeAsync(
            Call(User(Other, ["AssetManagement"])) with { MethodId = "Sample.Accounts/Account.Nope-1" },
            reader.CheckAsync, TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, anonymous.Error!.Code);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, unknown.Error!.Code);
        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task DefaultDeny_NoDeclaredAuthorization_OnlyAnAdministratorPasses()
    {
        var gateway = Gateway(authorization: null);
        var reader = new Counter();

        var owner = await gateway.InvokeAsync(Call(User(Owner, []), Owner), reader.CheckAsync, TestContext.Current.CancellationToken);
        var admin = await gateway.InvokeAsync(Call(User(Other, ["AdminPanelManagement"]), Owner), reader.CheckAsync,
            TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, owner.Error!.Code);
        Assert.True(admin.Success);
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task InvalidParameters_AreRefusedAfterTheTargetLookup_NothingIsDispatched_AndNoValueIsInTheError()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        var reader = new Counter();
        var call = Call(User(Other, ["AssetManagement"]), Owner) with
        {
            ParametersJson = "{\"newPassword\":123,\"surprise\":\"CANARY-SECRET-123\"}"
        };

        var response = await gateway.InvokeAsync(call, reader.CheckAsync, TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, response.Error!.Code);
        Assert.Equal("newPassword,surprise", response.Error.Parameters[CkMethodErrorParameters.Field]);
        Assert.Equal("unknown-member,wrong-type", response.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(1, reader.Reads); // validation runs after the lookup
        Assert.Empty(_dispatched);
        Assert.DoesNotContain("CANARY-SECRET-123", GraphQl(response));
        Assert.DoesNotContain("CANARY-SECRET-123", string.Join("\n", _logs));
    }

    [Fact]
    public async Task InvalidParameters_ForACallerWithoutTheRight_AreNeverJudged_TheAnswerIsForbidden()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        var reader = new Counter();
        var call = Call(User(Other, ["Nobody"]), Owner) with { ParametersJson = "{}" };

        var response = await gateway.InvokeAsync(call, reader.CheckAsync, TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task InvalidParameters_ForAMissingTarget_AnswerTargetNotFound_NotValidation()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        var reader = new Counter { Exists = false };
        var call = Call(User(Other, ["AssetManagement"]), Missing) with { ParametersJson = "{}" };

        var response = await gateway.InvokeAsync(call, reader.CheckAsync, TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodTargetNotFound, response.Error!.Code);
    }

    [Fact]
    public async Task MissingRequiredParameter_IsNamed()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });

        var response = await gateway.InvokeAsync(
            Call(User(Other, ["AssetManagement"]), Owner) with { ParametersJson = "{}" },
            new Counter().CheckAsync, TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, response.Error!.Code);
        Assert.Equal("newPassword", response.Error.Parameters[CkMethodErrorParameters.Field]);
        Assert.Equal("missing-required", response.Error.Parameters[CkMethodErrorParameters.Reason]);
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

    // ---- AB#7037: the data-permission check after the target load ----

    private static string Text(CkMethodResponse response) => JsonSerializer.Serialize(response.Error) + GraphQl(response);

    [Fact]
    public async Task HiddenTarget_AnswersExactlyLikeAMissingOne_NothingIsDispatched()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        var caller = User(Other, ["AssetManagement"]);
        // A hidden and a missing record both reach the gateway as "not visible"; the answer must not carry the target.
        var hidden = new Counter { Verdict = CkMethodTargetAccess.NotVisible };
        var missing = new Counter { Exists = false };

        var forHidden = await gateway.InvokeAsync(Call(caller, Owner), hidden.CheckAsync, TestContext.Current.CancellationToken);
        var forMissing = await gateway.InvokeAsync(Call(caller, Missing), missing.CheckAsync, TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodTargetNotFound, forHidden.Error!.Code);
        Assert.Equal(ErrorJson(forHidden), ErrorJson(forMissing));
        Assert.Equal(GraphQl(forHidden), GraphQl(forMissing));
        Assert.Empty(_dispatched);
    }

    [Fact]
    public async Task VisibleTargetWithoutWritePermission_IsForbiddenWithAFixedReason_NothingIsDispatched()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        var check = new Counter { Verdict = CkMethodTargetAccess.NotPermitted };

        var response = await gateway.InvokeAsync(Call(User(Other, ["AssetManagement"]), Owner), check.CheckAsync,
            TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
        Assert.Equal("data-permission", response.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(MethodId, response.Error.Parameters[CkMethodErrorParameters.MethodId]);
        Assert.Empty(_dispatched);
        Assert.DoesNotContain(Owner, Text(response));
    }

    [Fact]
    public async Task WithAWriteGrant_TheCallIsDispatched()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        var check = new Counter();

        var response = await gateway.InvokeAsync(Call(User(Other, ["AssetManagement"]), Owner), check.CheckAsync,
            TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Single(_dispatched);
    }

    [Fact]
    public async Task DataAction_IsWriteByDefault_AndReadWhenTheMethodDeclaresRead()
    {
        var undeclared = new Counter();
        var declaredWrite = new Counter();
        var declaredRead = new Counter();
        var caller = User(Other, ["AssetManagement"]);

        await Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] })
            .InvokeAsync(Call(caller, Owner), undeclared.CheckAsync, TestContext.Current.CancellationToken);
        await Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"], DataAccess = CkMethodDataAccessDto.Write })
            .InvokeAsync(Call(caller, Owner), declaredWrite.CheckAsync, TestContext.Current.CancellationToken);
        await Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"], DataAccess = CkMethodDataAccessDto.Read })
            .InvokeAsync(Call(caller, Owner), declaredRead.CheckAsync, TestContext.Current.CancellationToken);

        Assert.Equal([CkMethodDataAccessDto.Write], undeclared.Actions);
        Assert.Equal([CkMethodDataAccessDto.Write], declaredWrite.Actions);
        Assert.Equal([CkMethodDataAccessDto.Read], declaredRead.Actions);
    }

    [Fact]
    public async Task DataAction_WithoutAnyAuthorizationBlock_IsWrite()
    {
        var check = new Counter();

        await Gateway(authorization: null).InvokeAsync(Call(User(Other, ["AdminPanelManagement"]), Owner), check.CheckAsync,
            TestContext.Current.CancellationToken);

        Assert.Equal([CkMethodDataAccessDto.Write], check.Actions);
    }

    [Fact]
    public async Task AllowSelf_DoesNotExemptTheDataPermissionCheck()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { AllowSelf = true });
        var refused = new Counter { Verdict = CkMethodTargetAccess.NotPermitted };
        var granted = new Counter();

        var withoutGrant = await gateway.InvokeAsync(Call(User(Owner, []), Owner), refused.CheckAsync, TestContext.Current.CancellationToken);
        var withGrant = await gateway.InvokeAsync(Call(User(Owner, []), Owner), granted.CheckAsync, TestContext.Current.CancellationToken);

        Assert.Equal(1, refused.Reads); // the check ran although the caller is the target
        Assert.Equal("data-permission", withoutGrant.Error!.Parameters[CkMethodErrorParameters.Reason]);
        Assert.True(withGrant.Success);
        Assert.Single(_dispatched);
    }

    [Fact]
    public async Task StaticMethod_IsJudgedByTheTypeLevelCheck_Only()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["R"] });
        var allowed = new Counter();
        var refused = new Counter { Verdict = CkMethodTargetAccess.NotPermitted };
        var unreadable = new Counter { Verdict = CkMethodTargetAccess.NotVisible };
        var caller = User(Other, ["R"]);
        var call = Call(caller) with { TargetRtId = null };

        var pass = await gateway.InvokeAsync(call, allowed.CheckAsync, TestContext.Current.CancellationToken);
        var noGrant = await gateway.InvokeAsync(call, refused.CheckAsync, TestContext.Current.CancellationToken);
        var typeHidden = await gateway.InvokeAsync(call, unreadable.CheckAsync, TestContext.Current.CancellationToken);

        Assert.True(pass.Success);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, noGrant.Error!.Code);
        // there is no target to hide: a static method on a type the caller cannot read is a refusal, not "not found"
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, typeHidden.Error!.Code);
        Assert.Single(_dispatched);
    }

    [Fact]
    public async Task AuditOnlyViolation_Passes_AndTheSignalIsLogged_WithoutTheTarget()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        var check = new Counter { Verdict = new CkMethodTargetAccess(CkMethodTargetAccessOutcome.Allowed, AuditOnlyViolation: true) };

        var response = await gateway.InvokeAsync(Call(User(Other, ["AssetManagement"]), Owner), check.CheckAsync,
            TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Single(_dispatched);
        var all = string.Join("\n", _logs);
        Assert.Contains("audit only", all);
        Assert.Contains(MethodId, all);
        Assert.DoesNotContain(Owner, all);
        Assert.DoesNotContain("CANARY-SECRET-123", all);
        Assert.DoesNotContain("CANARY-TOKEN", all);
    }

    [Fact]
    public async Task AFaultInTheCheck_FailsTheCall_ItNeverPasses()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        var check = new Counter { Throw = new InvalidOperationException("boom") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.InvokeAsync(
            Call(User(Other, ["AssetManagement"]), Owner), check.CheckAsync, TestContext.Current.CancellationToken));

        Assert.Empty(_dispatched);
    }

    [Fact]
    public async Task RefusedByDataPermission_LogsAtInformationLevel_WithoutTheTargetTheParametersOrTheToken()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });

        await gateway.InvokeAsync(Call(User(Other, ["AssetManagement"]), Owner),
            new Counter { Verdict = CkMethodTargetAccess.NotPermitted }.CheckAsync, TestContext.Current.CancellationToken);
        await gateway.InvokeAsync(Call(User(Other, ["AssetManagement"]), Missing),
            new Counter { Exists = false }.CheckAsync, TestContext.Current.CancellationToken);

        var all = string.Join("\n", _logs);
        Assert.Contains("refused by a data permission", all);
        Assert.DoesNotContain(Owner, all);
        Assert.DoesNotContain(Missing, all);
        Assert.DoesNotContain("CANARY-SECRET-123", all);
        Assert.DoesNotContain("CANARY-TOKEN", all);
    }

    [Fact]
    public async Task RoleRefusal_NeverCallsTheCheck_ForAnExistingOrAMissingTarget()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        var check = new Counter();

        await gateway.InvokeAsync(Call(User(Other, ["Nobody"]), Owner), check.CheckAsync, TestContext.Current.CancellationToken);
        await gateway.InvokeAsync(Call(User(Other, ["Nobody"]), Missing), check.CheckAsync, TestContext.Current.CancellationToken);

        Assert.Equal(0, check.Reads);
    }

    [Fact]
    public async Task InvalidParameters_AreOnlyJudgedAfterTheDataPermission()
    {
        var gateway = Gateway(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });

        var response = await gateway.InvokeAsync(
            Call(User(Other, ["AssetManagement"]), Owner) with { ParametersJson = "{}" },
            new Counter { Verdict = CkMethodTargetAccess.NotPermitted }.CheckAsync, TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
    }

    [Fact]
    public void Checker_MapsTheEngineVerdict_AndTheDataAction()
    {
        Assert.Equal(CkMethodTargetAccessOutcome.Allowed, CkMethodTargetAccessChecker.Map(
            new RtDataPermissionVerdict(RtDataPermissionDecision.Allowed, RtDataPermissionReason.Granted)).Outcome);
        Assert.True(CkMethodTargetAccessChecker.Map(new RtDataPermissionVerdict(RtDataPermissionDecision.Allowed,
            RtDataPermissionReason.AuditOnly, true)).AuditOnlyViolation);
        Assert.Equal(CkMethodTargetAccessOutcome.NotVisible, CkMethodTargetAccessChecker.Map(
            new RtDataPermissionVerdict(RtDataPermissionDecision.NotVisible, RtDataPermissionReason.NoGrant)).Outcome);
        Assert.Equal(CkMethodTargetAccessOutcome.NotPermitted, CkMethodTargetAccessChecker.Map(
            new RtDataPermissionVerdict(RtDataPermissionDecision.NotPermitted, RtDataPermissionReason.NotOwner)).Outcome);
        Assert.Equal(CkMethodTargetAccessOutcome.NotPermitted, CkMethodTargetAccessChecker.Map(
            new RtDataPermissionVerdict((RtDataPermissionDecision)99, RtDataPermissionReason.Open)).Outcome);
        Assert.Equal(RtDataAction.Read, CkMethodTargetAccessChecker.ActionOf(CkMethodDataAccessDto.Read));
        Assert.Equal(RtDataAction.Write, CkMethodTargetAccessChecker.ActionOf(CkMethodDataAccessDto.Write));
    }
}
