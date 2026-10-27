using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.CkV2;

/// <summary>
///     AB#6915, GraphQL layer of the method path: real schema, real gateway and MongoDB, a dispatcher double behind it.
///     What a client can put into a method mutation, and what the service decides on its own. Results go to
///     <c>adversarial-results.md</c> next to the test assembly.
/// </summary>
[Collection(MethodGatewayCollection.Name)]
public class MethodAdversarialGraphQlTests
{
    private const string Field = "assetRepositoryIntegrationTestAccessTestAccounts";
    private const string Admin = "65f0c0ffee65f0c0ffee65f1";
    private const string Secret = "CANARY-SECRET-6915";

    private readonly MethodGatewayGraphQlTestFixture _fixture;

    public MethodAdversarialGraphQlTests(MethodGatewayGraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
        _fixture.Dispatcher.Reset();
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
            mutation { runtime { {{Field}} { create(entities: [{ name: "adv-{{Guid.NewGuid():N}}" }]) { rtId } } } }
            """);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return JObject.Parse(json).SelectToken($"data.runtime.{Field}.create[0].rtId")!.Value<string>()!;
    }

    private void Record(string caseId, string attack, string observed, [CallerMemberName] string test = "")
    {
        var sideEffect = _fixture.Dispatcher.Requests.Count == 0
            ? "none (nothing dispatched)"
            : $"dispatched {_fixture.Dispatcher.Requests.Count}";
        lock (typeof(MethodAdversarialGraphQlTests))
        {
            var path = Path.Combine(AppContext.BaseDirectory, "adversarial-results.md");
            var header = File.Exists(path) ? string.Empty : "| Case | Attack | Test | Observed | Side effect |\n|------|--------|------|----------|-------------|\n";
            File.AppendAllText(path, header + $"| {caseId} | {attack} | `MethodAdversarialGraphQlTests.{test}` | {observed} | {sideEffect} |\n", Encoding.UTF8);
        }
    }

    [Fact]
    public async Task C01_AForgedCallerMember_InTheMutationInput_IsAGraphQlValidationError_AndNothingIsDispatched()
    {
        var rtId = await CreateAccountAsync();

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Field}} {
              resetPassword(rtId: "{{rtId}}", input: { newPassword: "{{Secret}}", caller: { subjectId: "{{Admin}}", roles: ["AccessTestAdmin"] } }) { invocationId }
            } } }
            """, user: User(Admin.Replace('1', '2'), ["Nobody"]));

        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().NotBeNullOrEmpty(json);
        Record("C01", "Caller DTO as a member of the mutation input (administrator)", "GraphQL validation error: " + result.Errors![0].Code);
        _fixture.Dispatcher.Requests.Should().BeEmpty();
        json.Should().NotContain(Secret);
    }

    [Fact]
    public async Task C11_TheCallerCannotChooseTheDeadline_TheServiceSetsItFromTheDefinition()
    {
        var rtId = await CreateAccountAsync();

        var forged = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Field}} {
              resetPassword(rtId: "{{rtId}}", input: { newPassword: "x", deadlineUtc: "9999-12-31T23:59:59Z", timeoutSeconds: 86400 }) { invocationId }
            } } }
            """, user: User(Admin, ["AccessTestAdmin"]));
        var normal = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Field}} { resetPassword(rtId: "{{rtId}}", input: { newPassword: "x" }) { invocationId } } } }
            """, user: User(Admin, ["AccessTestAdmin"]));

        forged.Errors.Should().NotBeNullOrEmpty(); // no such input member
        normal.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(normal));
        var request = _fixture.Dispatcher.Requests.Should().ContainSingle().Subject;
        request.DeadlineUtc.Should().BeAfter(DateTime.UtcNow).And.BeBefore(DateTime.UtcNow.AddSeconds(30)); // timeoutSeconds: 5 in the model
        Record("C11", "deadlineUtc / timeoutSeconds as members of the input; deadline of a regular call = now + 5 s (definition)",
            "input member rejected; server deadline " + (request.DeadlineUtc - DateTime.UtcNow).TotalSeconds.ToString("F0") + " s");
    }

    [Fact]
    public async Task C08_AllowSelf_WithAnotherUsersId_InAnotherLetterCase_IsForbidden()
    {
        var rtId = await CreateAccountAsync();
        var other = User("65F0C0FFEE65F0C0FFEE65F9", []);

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Field}} { touch(rtId: "{{rtId}}") { invocationId } } } }
            """, user: other);

        var error = result.Errors.Should().ContainSingle(_fixture.SerializeGraphQl(result)).Subject;
        error.Code.Should().Be(CkMethodErrorCodes.MethodForbidden);
        _fixture.Dispatcher.Requests.Should().BeEmpty();
        Record("C08", "allowSelf (touch), another user's id in upper case, over GraphQL", error.Code!);
    }

    [Fact]
    public async Task C01c_NoUser_IsForbidden_AndNothingIsDispatched()
    {
        var rtId = await CreateAccountAsync();

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Field}} { touch(rtId: "{{rtId}}") { invocationId } } } }
            """, user: null);

        var error = result.Errors.Should().ContainSingle(_fixture.SerializeGraphQl(result)).Subject;
        error.Code.Should().Be(CkMethodErrorCodes.MethodForbidden);
        _fixture.Dispatcher.Requests.Should().BeEmpty();
        Record("C01c", "method mutation without an authenticated user", error.Code!);
    }
}
