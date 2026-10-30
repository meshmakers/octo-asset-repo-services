using System.Security.Claims;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.CkV2;

[CollectionDefinition(Name)]
public class MethodIdempotencyKeyCollection : ICollectionFixture<IdempotentMethodGraphQlTestFixture>
{
    public const string Name = "MethodIdempotencyKey";
}

/// <summary>
///     AB#7825 (F3.5, OD-4): the optional <c>idempotencyKey</c> argument of a typed method mutation reaches the request,
///     and the idempotency filter of the invocation pipeline applies the key rules behind the real schema, gateway and
///     in-process handler. A call without the argument is unchanged.
/// </summary>
[Collection(MethodIdempotencyKeyCollection.Name)]
public class MethodIdempotencyKeyTests
{
    private const string Field = "assetRepositoryIntegrationTestAccessTestAccounts";

    private readonly IdempotentMethodGraphQlTestFixture _fixture;

    public MethodIdempotencyKeyTests(IdempotentMethodGraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
        _fixture.Observation.Reset();
    }

    private static ClaimsPrincipal Admin(string sub = "65f0c0ffee65f0c0ffee65f1") => new(new ClaimsIdentity(
        [new Claim("sub", sub), new Claim("scope", "octo_api openid"), new Claim("role", "AccessTestAdmin")], "test"));

    private async Task<string> CreateAccountAsync()
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Field}} { create(entities: [{ name: "acct-{{Guid.NewGuid():N}}" }]) { rtId } } } }
            """);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return JObject.Parse(json).SelectToken($"data.runtime.{Field}.create[0].rtId")!.Value<string>()!;
    }

    private Task<ExecutionResult> ResetPasswordAsync(string rtId, string newPassword, string? key, ClaimsPrincipal? user = null)
    {
        var keyArgument = key == null ? string.Empty : $", idempotencyKey: \"{key}\"";
        return _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Field}} {
              resetPassword(rtId: "{{rtId}}", input: { newPassword: "{{newPassword}}" }{{keyArgument}}) { invocationId }
            } } }
            """, user: user ?? Admin());
    }

    private static string UniqueKey() => $"key-{Guid.NewGuid():N}";

    private static ExecutionError SingleError(ExecutionResult result) => result.Errors!.Should().ContainSingle().Subject;

    private static IDictionary<string, object?> ParametersOf(ExecutionError error) =>
        (IDictionary<string, object?>)error.Extensions!["parameters"]!;

    [Fact]
    public async Task SecondIdenticalKeyedCall_ReplaysTheFirstResult_AndTheHandlerRunsOnce()
    {
        var rtId = await CreateAccountAsync();
        var key = UniqueKey();

        var first = await ResetPasswordAsync(rtId, "correct-horse", key);
        var second = await ResetPasswordAsync(rtId, "correct-horse", key);

        first.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(first));
        second.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(second));
        JObject.Parse(_fixture.SerializeGraphQl(second)).SelectToken($"data.runtime.{Field}.resetPassword.invocationId")!
            .Value<string>().Should().NotBeNullOrEmpty();
        _fixture.Observation.Runs.Should().Be(1, "the replay answers from the store");
    }

    [Fact]
    public async Task CallsWithoutTheArgument_AreUnchanged_EveryCallRuns()
    {
        var rtId = await CreateAccountAsync();

        (await ResetPasswordAsync(rtId, "correct-horse", null)).Errors.Should().BeNullOrEmpty();
        (await ResetPasswordAsync(rtId, "correct-horse", null)).Errors.Should().BeNullOrEmpty();

        _fixture.Observation.Runs.Should().Be(2);
    }

    [Fact]
    public async Task AnotherKey_RunsTheMethodAgain()
    {
        var rtId = await CreateAccountAsync();

        (await ResetPasswordAsync(rtId, "correct-horse", UniqueKey())).Errors.Should().BeNullOrEmpty();
        (await ResetPasswordAsync(rtId, "correct-horse", UniqueKey())).Errors.Should().BeNullOrEmpty();

        _fixture.Observation.Runs.Should().Be(2);
    }

    [Fact]
    public async Task SameKeyOfAnotherCaller_IsAnotherScope_AndRuns()
    {
        var rtId = await CreateAccountAsync();
        var key = UniqueKey();

        (await ResetPasswordAsync(rtId, "correct-horse", key, Admin("65f0c0ffee65f0c0ffee65f1"))).Errors
            .Should().BeNullOrEmpty();
        (await ResetPasswordAsync(rtId, "correct-horse", key, Admin("65f0c0ffee65f0c0ffee65f2"))).Errors
            .Should().BeNullOrEmpty();

        _fixture.Observation.Runs.Should().Be(2);
    }

    [Fact]
    public async Task SameKeyWithChangedParameters_IsRefused_AsValidationFailedOnTheKey_AndNothingRuns()
    {
        var rtId = await CreateAccountAsync();
        var key = UniqueKey();
        (await ResetPasswordAsync(rtId, "correct-horse", key)).Errors.Should().BeNullOrEmpty();

        var changed = await ResetPasswordAsync(rtId, "battery-staple", key);

        var error = SingleError(changed);
        error.Code.Should().Be(CkMethodErrorCodes.MethodValidationFailed);
        ParametersOf(error)["field"].Should().Be("idempotencyKey");
        ParametersOf(error)["reason"].Should().Be("parameters-changed");
        _fixture.Observation.Runs.Should().Be(1);
        // neither the key nor a parameter value reaches the client
        _fixture.SerializeGraphQl(changed).Should().NotContain(key).And.NotContain("battery-staple");
    }

    [Fact]
    public async Task KeyOf129Characters_IsMethodValidationFailed_WithFieldIdempotencyKey_AndNothingRuns()
    {
        var rtId = await CreateAccountAsync();

        var result = await ResetPasswordAsync(rtId, "correct-horse", new string('k', 129));

        var error = SingleError(result);
        error.Code.Should().Be(CkMethodErrorCodes.MethodValidationFailed);
        error.Extensions!["code"].Should().Be(CkMethodErrorCodes.MethodValidationFailed);
        ParametersOf(error)["field"].Should().Be("idempotencyKey");
        ParametersOf(error)["reason"].Should().Be("key-too-long");
        _fixture.Observation.Runs.Should().Be(0);
    }

    [Fact]
    public async Task KeyOf128Characters_IsAccepted()
    {
        var rtId = await CreateAccountAsync();

        var result = await ResetPasswordAsync(rtId, "correct-horse", new string('k', 128));

        result.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(result));
        _fixture.Observation.Runs.Should().Be(1);
    }

    [Fact]
    public async Task KeyedCallOfAnUnauthorizedCaller_IsStillForbidden_TheKeyOpensNoWay()
    {
        var rtId = await CreateAccountAsync();
        var stranger = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "65f0c0ffee65f0c0ffee65f9"), new Claim("scope", "octo_api openid")], "test"));

        var result = await ResetPasswordAsync(rtId, "correct-horse", UniqueKey(), stranger);

        SingleError(result).Code.Should().Be(CkMethodErrorCodes.MethodForbidden);
        _fixture.Observation.Runs.Should().Be(0);
    }

    [Fact]
    public async Task UnexpectedHandlerFailure_BurnsTheKey_TheSecondCallDoesNotRunTheHandlerAgain()
    {
        var rtId = await CreateAccountAsync();
        var key = UniqueKey();

        var first = await ResetPasswordAsync(rtId, "boom", key);
        var second = await ResetPasswordAsync(rtId, "boom", key);

        SingleError(first).Code.Should().Be(CkMethodErrorCodes.MethodHandlerFailed);
        second.Errors.Should().NotBeNullOrEmpty();
        _fixture.Observation.Runs.Should().Be(1);
    }
}
