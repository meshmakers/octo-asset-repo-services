using System.Security.Claims;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL.Methods;

/// <summary>
///     AB#8986: <c>runtime.operations</c> on the parent-tenant path. A user token of a parent tenant with the role of the closed
///     method list reads, on a registered child tenant, only the records of the closed list (own records, or every caller's as an
///     administrator); a non-child, a missing role, a missing hierarchy reader or a failing one see nothing. Own-tenant callers
///     and service tokens are unchanged.
/// </summary>
public class CkMethodOperationReaderParentTenantTests
{
    private const string Parent = "parent1";
    private const string Child = "child1";
    private const string Grandchild = "grand1";
    private const string Dump = CkParentTenantAdministrationMethods.TenantRepositoryDump;
    private const string OtherMethod = "System.Identity/User.ChangePassword-1";
    private const string ParentAdminSub = "65f0c0ffee65f0c0ffee65fa";
    private const string ChildUserSub = "65f0c0ffee65f0c0ffee65fb";

    private readonly SteppingTimeProvider _time = new(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly CkMethodInMemoryOperationStore _store;

    public CkMethodOperationReaderParentTenantTests()
    {
        _store = new CkMethodInMemoryOperationStore(_time);
    }

    private sealed class Hierarchy(bool throws = false) : ITenantHierarchyReader
    {
        public Task<bool> IsChildTenantAsync(string parentTenantId, string tenantId)
        {
            if (throws)
            {
                throw new InvalidOperationException("registry down");
            }

            return Task.FromResult(
                (string.Equals(parentTenantId, Parent, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(tenantId, Child, StringComparison.OrdinalIgnoreCase)) ||
                (string.Equals(parentTenantId, Child, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(tenantId, Grandchild, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private CkMethodOperationReader Reader(ITenantHierarchyReader? hierarchy) =>
        new(_store, Options.Create(new CkMethodAuthorizationOptions()), NullLogger<CkMethodOperationReader>.Instance,
            hierarchy);

    private static ClaimsPrincipal ParentUser(string sub = ParentAdminSub, string tenant = Parent, params string[] roles) =>
        new(new ClaimsIdentity(
            new[] { new Claim("sub", sub), new Claim("scope", "octo_api"), new Claim("tenant_id", tenant) }
                .Concat(roles.Select(r => new Claim("role", r))), "test"));

    private static ClaimsPrincipal ParentAdmin() =>
        ParentUser(ParentAdminSub, Parent, CkParentTenantAdministrationMethods.RequiredRole, "AdminPanelManagement");

    private async Task<string> CreateAsync(string tenant, string method, string ownerSub)
    {
        var operationId = Guid.NewGuid().ToString("N");
        await _store.CreateAsync(new CkMethodOperationRecord
        {
            TenantId = tenant,
            OperationId = operationId,
            MethodId = method,
            InvocationId = Guid.NewGuid(),
            Owner = new CkMethodOperationOwner(ownerSub, null),
            HandlerInstanceId = "h1",
            State = CkMethodOperationState.Accepted,
            AcceptedAtUtc = _time.GetUtcNow().UtcDateTime,
            HeartbeatAtUtc = _time.GetUtcNow().UtcDateTime
        }, TestContext.Current.CancellationToken);
        return operationId;
    }

    private static CkMethodOperationListFilter All(bool allOwners = true, string? methodId = null) =>
        new(methodId, null, null, allOwners, 0, 50);

    [Fact]
    public async Task ParentOwner_ReadsItsClosedListOperation_ByHandle()
    {
        var id = await CreateAsync(Child, Dump, ParentAdminSub);

        var record = await Reader(new Hierarchy()).GetAsync(Child,
            ParentUser(ParentAdminSub, Parent, CkParentTenantAdministrationMethods.RequiredRole), id,
            TestContext.Current.CancellationToken);

        record.OperationId.Should().Be(id);
    }

    [Fact]
    public async Task ParentAdministrator_SeesOnlyClosedListRecordsOfTheChild()
    {
        var dumpOfChildUser = await CreateAsync(Child, Dump, ChildUserSub);
        var other = await CreateAsync(Child, OtherMethod, ChildUserSub);
        var reader = Reader(new Hierarchy());

        var page = await reader.ListAsync(Child, ParentAdmin(), All(), TestContext.Current.CancellationToken);
        page.Items.Select(i => i.OperationId).Should().Equal(dumpOfChildUser);
        page.TotalCount.Should().Be(1);

        (await reader.ListAsync(Child, ParentAdmin(), All(methodId: OtherMethod), TestContext.Current.CancellationToken))
            .Items.Should().BeEmpty();
        var act = () => reader.GetAsync(Child, ParentAdmin(), other, TestContext.Current.CancellationToken);
        (await act.Should().ThrowAsync<CkMethodOperationQueryException>()).Which.Code
            .Should().Be(CkMethodOperationQueryException.OperationNotFound);
        (await reader.GetAsync(Child, ParentAdmin(), dumpOfChildUser, TestContext.Current.CancellationToken))
            .OperationId.Should().Be(dumpOfChildUser);
    }

    public static TheoryData<string, string, bool, bool> Refused() => new()
    {
        // because, route tenant, with role, with hierarchy reader
        { "grandchild of an ordinary parent", Grandchild, true, true },
        { "a tenant that is not the parent's child", "unrelated1", true, true },
        { "parent without the role", Child, false, true },
        { "no hierarchy reader", Child, true, false }
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task ParentPath_OutsideTheRule_SeesNothing(string because, string routeTenant, bool role, bool hierarchy)
    {
        var id = await CreateAsync(routeTenant, Dump, ParentAdminSub);
        var user = role ? ParentAdmin() : ParentUser(ParentAdminSub, Parent, "AdminPanelManagement");
        var reader = Reader(hierarchy ? new Hierarchy() : null);

        var act = () => reader.GetAsync(routeTenant, user, id, TestContext.Current.CancellationToken);
        (await act.Should().ThrowAsync<CkMethodOperationQueryException>(because)).Which.Code
            .Should().Be(CkMethodOperationQueryException.OperationNotFound);
        (await reader.ListAsync(routeTenant, user, All(), TestContext.Current.CancellationToken)).Items
            .Should().BeEmpty(because);
        (await reader.ListAsync(routeTenant, user, All(allOwners: false), TestContext.Current.CancellationToken)).Items
            .Should().BeEmpty(because);
    }

    [Fact]
    public async Task FailingHierarchyReader_SeesNothing()
    {
        var id = await CreateAsync(Child, Dump, ParentAdminSub);
        var reader = Reader(new Hierarchy(throws: true));

        var act = () => reader.GetAsync(Child, ParentAdmin(), id, TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<CkMethodOperationQueryException>();
    }

    [Fact]
    public async Task OwnTenantAdministrator_StillSeesEveryMethod()
    {
        await CreateAsync(Child, Dump, ChildUserSub);
        await CreateAsync(Child, OtherMethod, ChildUserSub);

        var page = await Reader(new Hierarchy()).ListAsync(Child,
            ParentUser("65f0c0ffee65f0c0ffee65fc", Child, "AdminPanelManagement"), All(),
            TestContext.Current.CancellationToken);

        page.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task ServiceToken_IsNotJudgedByTheParentRule()
    {
        var operationId = Guid.NewGuid().ToString("N");
        await _store.CreateAsync(new CkMethodOperationRecord
        {
            TenantId = Child,
            OperationId = operationId,
            MethodId = OtherMethod,
            InvocationId = Guid.NewGuid(),
            Owner = new CkMethodOperationOwner(null, "svc"),
            HandlerInstanceId = "h1",
            State = CkMethodOperationState.Accepted,
            AcceptedAtUtc = _time.GetUtcNow().UtcDateTime,
            HeartbeatAtUtc = _time.GetUtcNow().UtcDateTime
        }, TestContext.Current.CancellationToken);
        var service = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("client_id", "svc"), new Claim("scope", "octo_api"), new Claim("tenant_id", Child)], "test"));

        (await Reader(new Hierarchy()).GetAsync(Child, service, operationId, TestContext.Current.CancellationToken))
            .OperationId.Should().Be(operationId);
    }
}
