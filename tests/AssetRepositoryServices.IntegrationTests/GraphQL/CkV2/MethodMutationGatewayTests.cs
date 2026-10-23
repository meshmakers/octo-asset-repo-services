using System.Security.Claims;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.CkV2;

[CollectionDefinition(Name)]
public class MethodGatewayCollection : ICollectionFixture<MethodGatewayGraphQlTestFixture>
{
    public const string Name = "MethodGateway";
}

/// <summary>
///     AB#7026 (F3.7): the resolver of a typed method mutation goes through the authorize-first gateway. Real schema,
///     real gateway (authorization, target lookup, parameter validation), MongoDB; only the handler side is a dispatcher
///     double. Test type <c>AccessTestAccount</c>: <c>resetPassword</c> (role <c>AccessTestAdmin</c> or the account
///     itself, sensitive parameters), <c>resetPassword2</c> (role, optional parameter, String result), <c>touch</c>
///     (<c>allowSelf</c> only).
/// </summary>
[Collection(MethodGatewayCollection.Name)]
public class MethodMutationGatewayTests
{
    private const string Field = "assetRepositoryIntegrationTestAccessTestAccounts";
    private const string Missing = "5f9b3b3b3b3b3b3b3b3b3b3b";
    private const string Secret = "CANARY-SECRET-7026";

    private readonly MethodGatewayGraphQlTestFixture _fixture;

    public MethodMutationGatewayTests(MethodGatewayGraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
        _fixture.Dispatcher.Reset();
        lock (_fixture.Logs)
        {
            _fixture.Logs.Clear();
        }
    }

    private static ClaimsPrincipal User(string sub, string[] roles, string scope = "octo_api openid")
    {
        var claims = new List<Claim> { new("sub", sub), new("scope", scope) };
        claims.AddRange(roles.Select(r => new Claim("role", r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private async Task<string> CreateAccountAsync()
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Field}} { create(entities: [{ name: "acct-{{Guid.NewGuid():N}}" }]) { rtId } } } }
            """);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return JObject.Parse(json).SelectToken($"data.runtime.{Field}.create[0].rtId")!.Value<string>()!;
    }

    private Task<ExecutionResult> ResetPasswordAsync(string rtId, ClaimsPrincipal? user) =>
        _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Field}} {
              resetPassword(rtId: "{{rtId}}", input: { newPassword: "{{Secret}}" }) { invocationId }
            } } }
            """, user: user);

    private static object? ExtensionOf(ExecutionError error, string key) =>
        error.Extensions != null && error.Extensions.TryGetValue(key, out var value) ? value : null;

    [Fact]
    public async Task Forbidden_IsTheSameAnswer_ForAnExistingAndAMissingTarget_AndNothingIsDispatched()
    {
        var existing = await CreateAccountAsync();
        var stranger = User("65f0c0ffee65f0c0ffee65f1", ["Nobody"]);

        var forExisting = await ResetPasswordAsync(existing, stranger);
        var forMissing = await ResetPasswordAsync(Missing, stranger);

        var a = forExisting.Errors!.Should().ContainSingle().Subject;
        var b = forMissing.Errors!.Should().ContainSingle().Subject;
        a.Code.Should().Be(CkMethodErrorCodes.MethodForbidden);
        ExtensionOf(a, "code").Should().Be(CkMethodErrorCodes.MethodForbidden);
        b.Message.Should().Be(a.Message);
        b.Code.Should().Be(a.Code);
        ((IDictionary<string, object?>)ExtensionOf(b, "parameters")!).Should()
            .BeEquivalentTo((IDictionary<string, object?>)ExtensionOf(a, "parameters")!);
        _fixture.Dispatcher.Requests.Should().BeEmpty();
        // no data either way
        JObject.Parse(_fixture.SerializeGraphQl(forExisting)).SelectToken($"data.runtime.{Field}.resetPassword")!
            .Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public async Task AuthorizedCaller_MissingTarget_GetsTargetNotFound_WithTheMethodId()
    {
        var admin = User("65f0c0ffee65f0c0ffee65f1", ["AccessTestAdmin"]);

        var result = await ResetPasswordAsync(Missing, admin);

        var error = result.Errors!.Should().ContainSingle().Subject;
        error.Code.Should().Be(CkMethodErrorCodes.MethodTargetNotFound);
        ((IDictionary<string, object?>)ExtensionOf(error, "parameters")!)["methodId"]!.ToString()
            .Should().Contain("AccessTestAccount.ResetPassword-1");
        _fixture.Dispatcher.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AllowSelf_PassesWhenTheCallerIdDiffersFromTheTargetIdOnlyInLetterCase()
    {
        var rtId = await CreateAccountAsync();
        var self = User(rtId.ToUpperInvariant(), []);

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Field}} { touch(rtId: "{{rtId}}") { invocationId } } } }
            """, user: self);

        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        JObject.Parse(json).SelectToken($"data.runtime.{Field}.touch.invocationId")!.Value<string>().Should().NotBeNullOrEmpty();
        _fixture.Dispatcher.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task AuthorizedCaller_GetsTheTypedResult_AndTheGatewayCallCarriesUserParametersAndTarget()
    {
        var rtId = await CreateAccountAsync();
        _fixture.Dispatcher.Script = r => CkMethodResponse.Succeeded(r.InvocationId, "h", "\"done\"");
        var admin = User("65f0c0ffee65f0c0ffee65f1", ["AccessTestAdmin"]);

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Field}} { resetPassword2(rtId: "{{rtId}}", input: { count: 3 }) { invocationId result } } } }
            """, user: admin);

        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        var node = JObject.Parse(json).SelectToken($"data.runtime.{Field}.resetPassword2")!;
        node["result"]!.Value<string>().Should().Be("done");
        var request = _fixture.Dispatcher.Requests.Should().ContainSingle().Subject;
        request.TargetRtId.Should().Be(rtId);
        request.Caller!.SubjectId.Should().Be("65f0c0ffee65f0c0ffee65f1");
        request.Caller.Roles.Should().Contain("AccessTestAdmin");
        request.MethodId.Should().Contain("AccessTestAccount.ResetPassword-2");
        request.ParametersJson.Should().Contain("\"count\":3");
        node["invocationId"]!.Value<string>().Should().Be(request.InvocationId.ToString("D"));
    }

    [Fact]
    public async Task StaticMethod_NeedsNoTarget()
    {
        var admin = User("65f0c0ffee65f0c0ffee65f1", ["AccessTestAdmin"]);

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Field}} { lookup { invocationId } } } }
            """, user: admin);

        result.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(result));
        _fixture.Dispatcher.Requests.Should().ContainSingle().Which.TargetRtId.Should().BeNull();
    }

    [Theory]
    [InlineData(CkMethodErrorCodes.MethodHandlerNotAvailable)]
    [InlineData(CkMethodErrorCodes.MethodTimeout)]
    [InlineData(CkMethodErrorCodes.MethodHandlerFailed)]
    [InlineData("PASSWORD_POLICY_VIOLATION")]
    public async Task FailedResponse_IsOneErrorWithCodeAndParameters_NoData(string code)
    {
        var rtId = await CreateAccountAsync();
        _fixture.Dispatcher.Script = r => CkMethodResponse.Failed(r.InvocationId, "h",
            CkMethodError.Create(code, (CkMethodErrorParameters.MethodId, r.MethodId), ("minLength", "12")));
        var admin = User("65f0c0ffee65f0c0ffee65f1", ["AccessTestAdmin"]);

        var result = await ResetPasswordAsync(rtId, admin);

        var json = _fixture.SerializeGraphQl(result);
        var error = result.Errors!.Should().ContainSingle(json).Subject;
        error.Code.Should().Be(code);
        ExtensionOf(error, "code").Should().Be(code);
        ((IDictionary<string, object?>)ExtensionOf(error, "parameters")!)["minLength"].Should().Be("12");
        json.Should().NotContain(Secret);
        JObject.Parse(json).SelectToken($"data.runtime.{Field}.resetPassword")!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public async Task TokenWithoutOctoApi_IsRefused_ForAMethodMutation_WhileTheScopeFlagIsOff()
    {
        var rtId = await CreateAccountAsync();
        var noScope = User("65f0c0ffee65f0c0ffee65f1", ["AccessTestAdmin"], scope: "openid profile");

        var method = await ResetPasswordAsync(rtId, noScope);
        // the generic mutation keeps its behavior: GraphQl:EnforceMutationScope is not set, so it is not refused
        var generic = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Field}} { create(entities: [{ name: "generic-{{Guid.NewGuid():N}}" }]) { rtId } } } }
            """, user: noScope);

        method.Errors!.Should().ContainSingle().Which.Code.Should().Be(CkMethodErrorCodes.MethodForbidden);
        _fixture.Dispatcher.Requests.Should().BeEmpty();
        generic.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(generic));
    }

    [Fact]
    public async Task RefusedCall_LeavesNoParameterValueAndNoTargetIdInLogsOrErrors()
    {
        var rtId = await CreateAccountAsync();
        lock (_fixture.Logs)
        {
            _fixture.Logs.Clear();
        }

        var result = await ResetPasswordAsync(rtId, User("65f0c0ffee65f0c0ffee65f1", ["Nobody"]));
        var missing = await ResetPasswordAsync(Missing, User("65f0c0ffee65f0c0ffee65f1", ["Nobody"]));

        var json = _fixture.SerializeGraphQl(result) + _fixture.SerializeGraphQl(missing);
        json.Should().NotContain(Secret).And.NotContain(rtId).And.NotContain(Missing);
        List<(LogLevel Level, string Text)> logs;
        lock (_fixture.Logs)
        {
            logs = _fixture.Logs.Where(l => l.Level >= LogLevel.Information).ToList();
        }

        logs.Should().NotBeEmpty();
        logs.Should().NotContain(l => l.Text.Contains(Secret) || l.Text.Contains(rtId) || l.Text.Contains(Missing));
    }
}
