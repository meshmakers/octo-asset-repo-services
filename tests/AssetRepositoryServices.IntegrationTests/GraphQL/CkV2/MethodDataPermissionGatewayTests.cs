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
public class MethodDataPermissionCollection : ICollectionFixture<MethodDataPermissionGraphQlTestFixture>
{
    public const string Name = "MethodDataPermission";
}

/// <summary>
///     AB#7037 (F3.4b): a method call never touches a record that data permissions hide or protect from the caller. Real
///     schema, real gateway, MongoDB and the real engine check; only the handler side is a dispatcher double. A
///     <c>resetPassword</c> (role <c>AccessTestAdmin</c> or the account itself) on an <c>AccessTestAccount</c> that a
///     policy protects must give the verdict of the generic mutation: no read grant is "not found" (like a missing
///     record), read without write is refused, a write grant passes.
/// </summary>
[Collection(MethodDataPermissionCollection.Name)]
public class MethodDataPermissionGatewayTests
{
    private const string Field = "assetRepositoryIntegrationTestAccessTestAccounts";
    private const string Missing = "5f9b3b3b3b3b3b3b3b3b3b3b";
    private const string Secret = "CANARY-SECRET-7037";
    private const string Admin = "AccessTestAdmin";

    private readonly MethodDataPermissionGraphQlTestFixture _fixture;

    public MethodDataPermissionGatewayTests(MethodDataPermissionGraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
        _fixture.Dispatcher.Reset();
        lock (_fixture.Logs)
        {
            _fixture.Logs.Clear();
        }
    }

    // Callers. All hold the declared method role, so the declared authorization passes and only the data permission decides.
    private static readonly ClaimsPrincipal Creator = User("creator", Admin, MethodDataPermissionGraphQlTestFixture.WriteAllRole,
        MethodDataPermissionGraphQlTestFixture.ReadAllRole);

    private static readonly ClaimsPrincipal Writer = User("writer", Admin, MethodDataPermissionGraphQlTestFixture.WriteAllRole,
        MethodDataPermissionGraphQlTestFixture.ReadAllRole);

    private static readonly ClaimsPrincipal ReadOnly = User("reader", Admin, MethodDataPermissionGraphQlTestFixture.ReadAllRole);

    private static readonly ClaimsPrincipal Blind = User("blind", Admin);

    private static readonly ClaimsPrincipal OwnerScoped = User("owner-scoped", Admin,
        MethodDataPermissionGraphQlTestFixture.ReadOwnRole, MethodDataPermissionGraphQlTestFixture.WriteOwnRole);

    private static ClaimsPrincipal User(string sub, params string[] roles)
    {
        var claims = new List<Claim> { new("sub", sub), new("scope", "octo_api openid") };
        claims.AddRange(roles.Select(r => new Claim("role", r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private async Task<string> CreateAccountAsync(ClaimsPrincipal creator)
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Field}} { create(entities: [{ name: "acct-{{Guid.NewGuid():N}}" }]) { rtId } } } }
            """, user: creator);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return JObject.Parse(json).SelectToken($"data.runtime.{Field}.create[0].rtId")!.Value<string>()!;
    }

    private Task<ExecutionResult> ResetPasswordAsync(string rtId, ClaimsPrincipal user) =>
        _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Field}} {
              resetPassword(rtId: "{{rtId}}", input: { newPassword: "{{Secret}}" }) { invocationId }
            } } }
            """, user: user);

    private Task<ExecutionResult> GenericRenameAsync(string rtId, ClaimsPrincipal user) =>
        _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Field}} { update(entities: [{ rtId: "{{rtId}}", item: { name: "renamed-{{Guid.NewGuid():N}}" } }]) { rtId } } } }
            """, user: user);

    private static object? ExtensionOf(ExecutionError error, string key) =>
        error.Extensions != null && error.Extensions.TryGetValue(key, out var value) ? value : null;

    private static IDictionary<string, object?> ParametersOf(ExecutionError error) =>
        (IDictionary<string, object?>)ExtensionOf(error, "parameters")!;

    [Fact]
    public async Task HiddenTarget_AnswersLikeAMissingOne_NothingIsDispatched_AndTheGenericMutationAgrees()
    {
        var existing = await CreateAccountAsync(Creator);

        var hidden = await ResetPasswordAsync(existing, Blind);
        var missing = await ResetPasswordAsync(Missing, Blind);

        var a = hidden.Errors!.Should().ContainSingle().Subject;
        var b = missing.Errors!.Should().ContainSingle().Subject;
        a.Code.Should().Be(CkMethodErrorCodes.MethodTargetNotFound);
        b.Code.Should().Be(a.Code);
        b.Message.Should().Be(a.Message);
        ParametersOf(b).Should().BeEquivalentTo(ParametersOf(a));
        _fixture.Dispatcher.Requests.Should().BeEmpty();

        // the generic mutation gives the same verdict: a record the caller cannot see is not found
        var generic = await GenericRenameAsync(existing, Blind);
        generic.Errors.Should().NotBeNullOrEmpty();
        generic.Errors![0].Code.Should().Be("ASSET1001");
    }

    [Fact]
    public async Task ARecordOfAnotherOwner_IsHiddenFromAnOwnerScopedCaller_ButItsOwnRecordPasses()
    {
        var foreign = await CreateAccountAsync(Creator);
        var own = await CreateAccountAsync(OwnerScoped);

        var hidden = await ResetPasswordAsync(foreign, OwnerScoped);
        var ownResult = await ResetPasswordAsync(own, OwnerScoped);

        hidden.Errors!.Should().ContainSingle().Subject.Code.Should().Be(CkMethodErrorCodes.MethodTargetNotFound);
        ownResult.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(ownResult));
        _fixture.Dispatcher.Requests.Should().ContainSingle().Which.TargetRtId.Should().Be(own);
    }

    [Fact]
    public async Task VisibleTargetWithoutWriteGrant_IsForbiddenWithAFixedReason_NothingIsDispatched_AndTheGenericMutationRefuses()
    {
        var existing = await CreateAccountAsync(Creator);

        var result = await ResetPasswordAsync(existing, ReadOnly);

        var error = result.Errors!.Should().ContainSingle().Subject;
        error.Code.Should().Be(CkMethodErrorCodes.MethodForbidden);
        ParametersOf(error)[CkMethodErrorParameters.Reason].Should().Be("data-permission");
        _fixture.Dispatcher.Requests.Should().BeEmpty();

        var generic = await GenericRenameAsync(existing, ReadOnly);
        generic.Errors.Should().NotBeNullOrEmpty("the generic mutation refuses the same caller");
    }

    [Fact]
    public async Task WithAWriteGrant_TheCallIsDispatched_AndTheGenericMutationPasses()
    {
        var existing = await CreateAccountAsync(Creator);

        var result = await ResetPasswordAsync(existing, Writer);

        result.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(result));
        _fixture.Dispatcher.Requests.Should().ContainSingle().Which.TargetRtId.Should().Be(existing);
        var generic = await GenericRenameAsync(existing, Writer);
        generic.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(generic));
    }

    [Fact]
    public async Task AllowSelf_DoesNotExemptTheCheck_ARoleWithoutGrantOnTheOwnRecordIsRefused()
    {
        var existing = await CreateAccountAsync(Creator);
        // touch: allowSelf only, no role. The caller is the account itself and holds no grant, or read only.
        var selfBlind = User(existing);
        var selfReadOnly = User(existing, MethodDataPermissionGraphQlTestFixture.ReadAllRole);

        var blind = await TouchAsync(existing, selfBlind);
        var readOnly = await TouchAsync(existing, selfReadOnly);

        blind.Errors!.Should().ContainSingle().Subject.Code.Should().Be(CkMethodErrorCodes.MethodTargetNotFound);
        var refused = readOnly.Errors!.Should().ContainSingle().Subject;
        refused.Code.Should().Be(CkMethodErrorCodes.MethodForbidden);
        ParametersOf(refused)[CkMethodErrorParameters.Reason].Should().Be("data-permission");
        _fixture.Dispatcher.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AllowSelf_WithAWriteGrant_Passes()
    {
        var existing = await CreateAccountAsync(Creator);
        var self = User(existing, MethodDataPermissionGraphQlTestFixture.ReadAllRole,
            MethodDataPermissionGraphQlTestFixture.WriteAllRole);

        var result = await TouchAsync(existing, self);

        result.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(result));
        _fixture.Dispatcher.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task StaticMethod_IsJudgedOnTheDeclaringTypeOnly()
    {
        var withWrite = await LookupAsync(Writer);
        var withoutWrite = await LookupAsync(ReadOnly);
        var withoutRead = await LookupAsync(Blind);

        withWrite.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(withWrite));
        withoutWrite.Errors!.Should().ContainSingle().Subject.Code.Should().Be(CkMethodErrorCodes.MethodForbidden);
        withoutRead.Errors!.Should().ContainSingle().Subject.Code.Should().Be(CkMethodErrorCodes.MethodForbidden);
        _fixture.Dispatcher.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task RoleRefusal_StaysTheSame_ForAProtectedExistingAndAMissingTarget()
    {
        var existing = await CreateAccountAsync(Creator);
        var stranger = User("stranger", "Nobody", MethodDataPermissionGraphQlTestFixture.WriteAllRole,
            MethodDataPermissionGraphQlTestFixture.ReadAllRole);

        var forExisting = await ResetPasswordAsync(existing, stranger);
        var forMissing = await ResetPasswordAsync(Missing, stranger);

        var a = forExisting.Errors!.Should().ContainSingle().Subject;
        var b = forMissing.Errors!.Should().ContainSingle().Subject;
        a.Code.Should().Be(CkMethodErrorCodes.MethodForbidden);
        b.Message.Should().Be(a.Message);
        ParametersOf(b).Should().BeEquivalentTo(ParametersOf(a));
        ParametersOf(a)[CkMethodErrorParameters.Reason].Should().Be("not-authorized");
        _fixture.Dispatcher.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task NothingAtInformationLevelOrAbove_NamesTheParameterTheTokenOrTheTargetOfARefusedCall()
    {
        var existing = await CreateAccountAsync(Creator);

        var hidden = await ResetPasswordAsync(existing, Blind);
        var forbidden = await ResetPasswordAsync(existing, ReadOnly);

        string[] texts =
        [
            _fixture.SerializeGraphQl(hidden), _fixture.SerializeGraphQl(forbidden),
            .. hidden.Errors!.Select(e => e.ToString()), .. forbidden.Errors!.Select(e => e.ToString())
        ];
        List<(LogLevel Level, string Text)> logs;
        lock (_fixture.Logs)
        {
            logs = _fixture.Logs.Where(l => l.Level >= LogLevel.Information).ToList();
        }

        var all = string.Join("\n", texts.Concat(logs.Select(l => l.Text)));
        all.Should().NotContain(Secret).And.NotContain(existing);
        logs.Should().Contain(l => l.Text.Contains("refused by a data permission"));
    }

    private Task<ExecutionResult> TouchAsync(string rtId, ClaimsPrincipal user) =>
        _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Field}} { touch(rtId: "{{rtId}}") { invocationId } } } }
            """, user: user);

    private Task<ExecutionResult> LookupAsync(ClaimsPrincipal user) =>
        _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Field}} { lookup { invocationId } } } }
            """, user: user);
}
