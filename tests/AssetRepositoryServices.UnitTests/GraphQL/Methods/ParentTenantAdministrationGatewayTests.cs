using System.Security.Claims;
using FakeItEasy;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Audit;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL.Methods;

/// <summary>
///     AB#8986: the gateway applies the parent-tenant administration rule of octo-common-services. A user token of a parent tenant
///     calls a method of the closed list on a registered child tenant (roles judged as for every caller); every other method, a
///     sibling, a grandchild, a service token and a wrong role stay refused, nothing read or dispatched.
/// </summary>
public class ParentTenantAdministrationGatewayTests
{
    private const string Dump = CkParentTenantAdministrationMethods.TenantRepositoryDump;
    private const string OtherMethod = "System.Identity/User.ChangePassword-1";
    private const string Parent = "parent1";
    private const string Child = "child1";
    private const string Sibling = "child2";
    private const string Grandchild = "grand1";

    private readonly ICkMethodDispatcher _dispatcher = A.Fake<ICkMethodDispatcher>();
    private readonly List<CkMethodRequest> _dispatched = [];
    private int _targetReads;

    public ParentTenantAdministrationGatewayTests()
    {
        A.CallTo(() => _dispatcher.InvokeAsync(A<CkMethodRequest>._, A<CancellationToken>._))
            .ReturnsLazily((CkMethodRequest r, CancellationToken _) =>
            {
                _dispatched.Add(r);
                return Task.FromResult(CkMethodResponse.Succeeded(r.InvocationId, "handler-1", "null"));
            });
    }

    private sealed class Hierarchy : ITenantHierarchyReader
    {
        public Task<bool> IsChildTenantAsync(string parentTenantId, string tenantId) =>
            Task.FromResult(
                (string.Equals(parentTenantId, Parent, StringComparison.OrdinalIgnoreCase) &&
                 (string.Equals(tenantId, Child, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(tenantId, Sibling, StringComparison.OrdinalIgnoreCase))) ||
                (string.Equals(parentTenantId, Child, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(tenantId, Grandchild, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class Source : ICkMethodAuthorizationDefinitionSource
    {
        public CkMethodDto? Find(string tenantId, string ckTypeId, string qualifiedMethodId) => new()
        {
            MethodId = qualifiedMethodId[(qualifiedMethodId.LastIndexOf('.') + 1)..],
            Kind = CkMethodKindDto.Static,
            Authorization = new CkMethodAuthorizationDto { Roles = [CkParentTenantAdministrationMethods.RequiredRole] }
        };
    }

    private ICkMethodGateway Gateway(ITenantHierarchyReader? hierarchy)
    {
        var timeout = A.Fake<ICkMethodTimeoutResolver>();
        A.CallTo(() => timeout.GetDeadlineUtc(A<string>._, A<string>._, A<string>._))
            .Returns(DateTime.UtcNow.AddSeconds(15));
        return new CkMethodGateway(
            new CkMethodAuthorizationEvaluator(Options.Create(new CkMethodAuthorizationOptions())),
            new Source(), timeout, _dispatcher, A.Fake<ICkMethodAuditRecorder>(),
            NullLogger<CkMethodGateway>.Instance, tenantOptions: Options.Create(new TenantAuthorizationOptions()),
            hierarchyReader: hierarchy);
    }

    private static ClaimsPrincipal Token(string tenant, bool user = true, bool role = true)
    {
        var claims = new List<Claim> { new("tenant_id", tenant), new("scope", "octo_api") };
        claims.Add(user ? new Claim("sub", "65f0c0ffee65f0c0ffee65f1") : new Claim("client_id", "parent-client"));
        if (role)
        {
            claims.Add(new Claim("role", CkParentTenantAdministrationMethods.RequiredRole));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private Task<CkMethodResponse> InvokeAsync(ICkMethodGateway gateway, ClaimsPrincipal user, string callTenant,
        string methodId = Dump) =>
        gateway.InvokeAsync(
            new CkMethodGatewayCall(callTenant, "System.Bot/TenantRepository", methodId, null, "{}", user, "token"),
            (_, _) =>
            {
                Interlocked.Increment(ref _targetReads);
                return Task.FromResult(CkMethodTargetAccess.Allowed);
            }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ParentOnItsChild_ClosedListMethod_IsDispatchedForTheChild()
    {
        var response = await InvokeAsync(Gateway(new Hierarchy()), Token(Parent), Child);

        Assert.True(response.Success);
        var request = Assert.Single(_dispatched);
        Assert.Equal(Child, request.TenantId);
        Assert.Equal(Dump, request.MethodId);
    }

    public static TheoryData<string, string, bool, bool, string, string> Refused() => new()
    {
        // because, call tenant, user token, role, method, expected reason
        { "method not on the closed list", Child, true, true, OtherMethod, CkMethodTokenReasons.TenantNotAllowed },
        { "grandchild of an ordinary parent", Grandchild, true, true, Dump, CkMethodTokenReasons.TenantNotAllowed },
        { "a tenant that is not the parent's child", "unrelated1", true, true, Dump, CkMethodTokenReasons.TenantNotAllowed },
        { "service token of the parent", Child, false, true, Dump, CkMethodTokenReasons.TenantNotAllowed },
        { "without the role", Child, true, false, Dump, "not-authorized" }
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task ParentPath_OutsideTheRule_IsRefused_AndDispatchesNothing(string because, string callTenant, bool user,
        bool role, string methodId, string expectedReason)
    {
        var response = await InvokeAsync(Gateway(new Hierarchy()), Token(Parent, user, role), callTenant, methodId);

        Assert.False(response.Success, because);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
        Assert.Equal(expectedReason, response.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Empty(_dispatched);
        Assert.Equal(0, _targetReads);
    }

    [Fact]
    public async Task ChildOnItsParent_IsRefused()
    {
        var response = await InvokeAsync(Gateway(new Hierarchy()), Token(Child), Parent);

        Assert.Equal(CkMethodTokenReasons.TenantNotAllowed, response.Error!.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Empty(_dispatched);
    }

    [Fact]
    public async Task WithoutAHierarchyReader_TheParentIsRefused()
    {
        var response = await InvokeAsync(Gateway(null), Token(Parent), Child);

        Assert.Equal(CkMethodTokenReasons.TenantNotAllowed, response.Error!.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Empty(_dispatched);
    }
}
