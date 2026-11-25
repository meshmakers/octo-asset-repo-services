using System.Security.Claims;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.Mutations;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.CkV2;

/// <summary>
///     A request that dispatched a CK method call is never executed a second time by the transient transaction retry:
///     the method handler may call other systems. Real schema, real method resolver and gateway; only the handler
///     side is a dispatcher double, which counts the dispatches.
/// </summary>
[Collection(MethodGatewayCollection.Name)]
public class MethodCallTransientRetryTests
{
    private const string Field = "assetRepositoryIntegrationTestAccessTestAccounts";

    private readonly MethodGatewayGraphQlTestFixture _fixture;

    public MethodCallTransientRetryTests(MethodGatewayGraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
        _fixture.Dispatcher.Reset();
    }

    [Fact]
    public async Task MethodCall_WithATransientTransactionError_IsNotRetried_AndTheHandlerIsDispatchedOnce()
    {
        var admin = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "65f0c0ffee65f0c0ffee65f1"), new Claim("scope", "octo_api openid"),
            new Claim("tenant_id", _fixture.SystemTenantId), new Claim("role", "AccessTestAdmin")
        ], "test"));
        var injector = new TransientErrorInjectingListener(_ => TransientErrorInjectingListener.TransientException(),
            failAttempts: int.MaxValue);

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{Field}} { lookup { invocationId } } } }
            """, user: admin, configure: options => options.Listeners.Add(injector));

        injector.Attempts.Should().Be(1, "the resolver marked the request before it dispatched the method");
        _fixture.Dispatcher.Requests.Should().ContainSingle();
        result.Errors.Should().ContainSingle().Which.Message.Should().Be("injected");
    }
}
