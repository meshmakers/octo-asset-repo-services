using System.Security.Claims;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.Options;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL.Methods;

/// <summary>
///     AB#8444 (OD-5 point 5): who may read which operation record through <c>runtime.operations</c>. The caller who
///     started it and tenant administrators read it; everybody else, another tenant, an expired and an unknown id get the
///     same <c>OPERATION_NOT_FOUND</c>. Callers list their own operations, administrators every caller's.
/// </summary>
public class CkMethodOperationReaderTests
{
    private const string Tenant = "meshtest";
    private const string Method = "Sample/Thing.Export-1";
    private const string OtherMethod = "Sample/Thing.Import-1";
    private const string OwnerSub = "65f0c0ffee65f0c0ffee65f1";
    private const string OtherSub = "65f0c0ffee65f0c0ffee65f2";

    private readonly SteppingTimeProvider _time = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly CkMethodInMemoryOperationStore _store;
    private readonly CkMethodOperationReader _reader;

    public CkMethodOperationReaderTests()
    {
        _store = new CkMethodInMemoryOperationStore(_time);
        _reader = new CkMethodOperationReader(_store, Options.Create(new CkMethodAuthorizationOptions()));
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private static ClaimsPrincipal User(string sub, params string[] roles) => new(new ClaimsIdentity(
        new[] { new Claim("sub", sub), new Claim("scope", "octo_api") }
            .Concat(roles.Select(r => new Claim("role", r))), "test"));

    private static ClaimsPrincipal Client(string clientId, params string[] roles) => new(new ClaimsIdentity(
        new[] { new Claim("client_id", clientId), new Claim("scope", "octo_api") }
            .Concat(roles.Select(r => new Claim("role", r))), "test"));

    private static ClaimsPrincipal Admin(string sub = "65f0c0ffee65f0c0ffee65f9") => User(sub, "AdminPanelManagement");

    private async Task<string> CreateAsync(CkMethodOperationOwner owner, string tenant = Tenant, string method = Method,
        DateTime? acceptedAt = null)
    {
        var operationId = Guid.NewGuid().ToString("N");
        await _store.CreateAsync(new CkMethodOperationRecord
        {
            TenantId = tenant,
            OperationId = operationId,
            MethodId = method,
            InvocationId = Guid.NewGuid(),
            Owner = owner,
            HandlerInstanceId = "h1",
            State = CkMethodOperationState.Accepted,
            AcceptedAtUtc = acceptedAt ?? Now,
            HeartbeatAtUtc = acceptedAt ?? Now
        }, TestContext.Current.CancellationToken);
        return operationId;
    }

    private static CkMethodOperationOwner Sub(string sub) => new(sub, null);

    private async Task<CkMethodOperationQueryException> NotFoundAsync(ClaimsPrincipal user, string operationId,
        string tenant = Tenant)
    {
        var act = () => _reader.GetAsync(tenant, user, operationId, TestContext.Current.CancellationToken);
        var exception = (await act.Should().ThrowAsync<CkMethodOperationQueryException>()).Which;
        exception.Code.Should().Be(CkMethodOperationQueryException.OperationNotFound);
        return exception;
    }

    [Fact]
    public async Task Owner_ReadsTheOperation()
    {
        var id = await CreateAsync(Sub(OwnerSub));

        var record = await _reader.GetAsync(Tenant, User(OwnerSub), id, TestContext.Current.CancellationToken);

        record.OperationId.Should().Be(id);
        record.MethodId.Should().Be(Method);
    }

    [Fact]
    public async Task TenantAdministrator_ReadsAnotherCallersOperation()
    {
        var id = await CreateAsync(Sub(OwnerSub));

        var record = await _reader.GetAsync(Tenant, Admin(), id, TestContext.Current.CancellationToken);

        record.OperationId.Should().Be(id);
    }

    [Fact]
    public async Task ConfiguredAdministratorRoles_ReplaceTheDefault()
    {
        var reader = new CkMethodOperationReader(_store,
            Options.Create(new CkMethodAuthorizationOptions { AdministratorRoles = ["OpsAdmin"] }));
        var id = await CreateAsync(Sub(OwnerSub));

        (await reader.GetAsync(Tenant, User(OtherSub, "OpsAdmin"), id, TestContext.Current.CancellationToken))
            .OperationId.Should().Be(id);
        var act = () => reader.GetAsync(Tenant, Admin(), id, TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<CkMethodOperationQueryException>();
    }

    [Fact]
    public async Task ClientCredentialsOwner_ReadsItsOperation_AUserWhoseSubIsTheClientIdDoesNot()
    {
        var id = await CreateAsync(new CkMethodOperationOwner(null, "pipeline-client"));

        (await _reader.GetAsync(Tenant, Client("pipeline-client"), id, TestContext.Current.CancellationToken))
            .OperationId.Should().Be(id);
        await NotFoundAsync(User("pipeline-client"), id);
    }

    [Fact]
    public async Task ForeignExpiredUnknownAndOtherTenantIds_AreTheSameNotFound()
    {
        var foreign = await CreateAsync(Sub(OwnerSub));
        var otherTenant = await CreateAsync(Sub(OwnerSub), tenant: "othertenant");
        var expired = await CreateAsync(Sub(OwnerSub));
        await _store.CompleteAsync(Tenant, expired, new CkMethodOperationCompletion
        {
            State = CkMethodOperationState.Succeeded,
            CompletedAtUtc = Now,
            ExpiresAtUtc = Now.AddHours(1)
        }, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromHours(2));

        var errors = new[]
        {
            await NotFoundAsync(User(OtherSub), foreign),
            await NotFoundAsync(User(OwnerSub), otherTenant),
            await NotFoundAsync(Admin(), otherTenant),
            await NotFoundAsync(User(OwnerSub), expired),
            await NotFoundAsync(Admin(), expired),
            await NotFoundAsync(User(OwnerSub), "does-not-exist"),
            await NotFoundAsync(User(OwnerSub), new string('x', 129)),
            await NotFoundAsync(User(OwnerSub), "  ")
        };

        // byte-identical: no existence oracle
        errors.Select(e => (e.Code, e.Message, string.Join(",", e.Parameters))).Distinct().Should().ContainSingle();
        errors[0].Parameters.Should().BeEmpty();
    }

    [Fact]
    public async Task Unauthenticated_GetsNotFound_AndAnEmptyList()
    {
        var id = await CreateAsync(Sub(OwnerSub));
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        await NotFoundAsync(anonymous, id);
        var page = await _reader.ListAsync(Tenant, anonymous, Filter(), TestContext.Current.CancellationToken);
        page.Items.Should().BeEmpty();
        page.TotalCount.Should().Be(0);
    }

    private static CkMethodOperationListFilter Filter(string? method = null, CkMethodOperationState? state = null,
        DateTime? since = null, bool allOwners = false, int skip = 0, int take = 50) =>
        new(method, state, since, allOwners, skip, take);

    [Fact]
    public async Task List_ReturnsOnlyTheCallersOwnOperations_FilteredByMethodStateAndSince()
    {
        var mine = await CreateAsync(Sub(OwnerSub));
        var mineOtherMethod = await CreateAsync(Sub(OwnerSub), method: OtherMethod);
        var mineOld = await CreateAsync(Sub(OwnerSub), acceptedAt: Now.AddDays(-1));
        await CreateAsync(Sub(OtherSub));
        await _store.MarkRunningAsync(Tenant, mine, Now, TestContext.Current.CancellationToken);

        var all = await _reader.ListAsync(Tenant, User(OwnerSub), Filter(), TestContext.Current.CancellationToken);
        all.Items.Select(r => r.OperationId).Should().BeEquivalentTo([mine, mineOtherMethod, mineOld]);
        all.TotalCount.Should().Be(3);

        (await _reader.ListAsync(Tenant, User(OwnerSub), Filter(method: Method), TestContext.Current.CancellationToken))
            .Items.Select(r => r.OperationId).Should().BeEquivalentTo([mine, mineOld]);
        (await _reader.ListAsync(Tenant, User(OwnerSub), Filter(state: CkMethodOperationState.Running),
            TestContext.Current.CancellationToken)).Items.Select(r => r.OperationId).Should().Equal(mine);
        (await _reader.ListAsync(Tenant, User(OwnerSub), Filter(since: Now.AddHours(-1)),
            TestContext.Current.CancellationToken)).Items.Select(r => r.OperationId)
            .Should().BeEquivalentTo([mine, mineOtherMethod]);
    }

    [Fact]
    public async Task List_AdministratorWithoutAllOwners_SeesOnlyOwnOperations_WithAllOwnersEveryCallersOfTheTenant()
    {
        await CreateAsync(Sub(OwnerSub));
        await CreateAsync(Sub(OtherSub));
        await CreateAsync(Sub(OwnerSub), tenant: "othertenant");
        var admins = await CreateAsync(Sub("65f0c0ffee65f0c0ffee65f9"));

        (await _reader.ListAsync(Tenant, Admin(), Filter(), TestContext.Current.CancellationToken))
            .Items.Select(r => r.OperationId).Should().Equal(admins);
        var all = await _reader.ListAsync(Tenant, Admin(), Filter(allOwners: true), TestContext.Current.CancellationToken);
        all.TotalCount.Should().Be(3, "every caller of this tenant, never another tenant");
    }

    [Fact]
    public async Task List_AllOwners_IsRefusedForANonAdministrator()
    {
        var act = () => _reader.ListAsync(Tenant, User(OwnerSub), Filter(allOwners: true),
            TestContext.Current.CancellationToken);

        var exception = (await act.Should().ThrowAsync<CkMethodOperationQueryException>()).Which;
        exception.Code.Should().Be(CkMethodOperationQueryException.OperationQueryRefused);
        exception.Parameters["reason"].Should().Be(CkMethodOperationQueryException.ReasonAdministratorsOnly);
    }

    [Theory]
    [InlineData(-1, 50, "skip")]
    [InlineData(0, 0, "take")]
    [InlineData(0, 201, "take")]
    public async Task List_InvalidPaging_IsRefused(int skip, int take, string argument)
    {
        var act = () => _reader.ListAsync(Tenant, User(OwnerSub), Filter(skip: skip, take: take),
            TestContext.Current.CancellationToken);

        var exception = (await act.Should().ThrowAsync<CkMethodOperationQueryException>()).Which;
        exception.Code.Should().Be(CkMethodOperationQueryException.OperationQueryRefused);
        exception.Parameters["reason"].Should().Be(CkMethodOperationQueryException.ReasonInvalidPaging);
        exception.Parameters["argument"].Should().Be(argument);
    }

    [Fact]
    public async Task List_PagesNewestFirst()
    {
        var older = await CreateAsync(Sub(OwnerSub), acceptedAt: Now.AddMinutes(-2));
        var newer = await CreateAsync(Sub(OwnerSub), acceptedAt: Now.AddMinutes(-1));

        var first = await _reader.ListAsync(Tenant, User(OwnerSub), Filter(take: 1), TestContext.Current.CancellationToken);
        var second = await _reader.ListAsync(Tenant, User(OwnerSub), Filter(skip: 1, take: 1),
            TestContext.Current.CancellationToken);

        first.Items.Select(r => r.OperationId).Should().Equal(newer);
        second.Items.Select(r => r.OperationId).Should().Equal(older);
        first.TotalCount.Should().Be(2);
    }

    [Fact]
    public void ErrorOfTheQuery_HasCodeAndSortedParameters_InTheExtensions()
    {
        var error = CkMethodOperationQueryField.CreateError(CkMethodOperationQueryException.Refused(
            CkMethodOperationQueryException.ReasonInvalidPaging, ("argument", "take")));

        error.Code.Should().Be(CkMethodOperationQueryException.OperationQueryRefused);
        error.Extensions!["code"].Should().Be(CkMethodOperationQueryException.OperationQueryRefused);
        ((IDictionary<string, object?>)error.Extensions["parameters"]!).Keys.Should().Equal("argument", "reason");
    }

    [Fact]
    public void ResultJson_BecomesAPlainValueTree()
    {
        CkMethodOperationGraphType.ToValue(null).Should().BeNull();
        CkMethodOperationGraphType.ToValue("\"file.zip\"").Should().Be("file.zip");
        CkMethodOperationGraphType.ToValue("42").Should().Be(42L);
        var tree = (IDictionary<string, object?>)CkMethodOperationGraphType.ToValue(
            """{"name":"a","size":1.5,"tags":["x"],"ok":true,"none":null}""")!;
        tree["name"].Should().Be("a");
        tree["size"].Should().Be(1.5d);
        ((IEnumerable<object?>)tree["tags"]!).Should().Equal("x");
        tree["ok"].Should().Be(true);
        tree["none"].Should().BeNull();
    }

    [Fact]
    public void MethodError_IsKeptAsDataOfTheOperation()
    {
        // the error type reads the stored CkMethodError as-is (code + parameters), sorted for a stable answer
        var error = CkMethodError.Create(CkMethodErrorCodes.MethodOutcomeUnknown,
            (CkMethodErrorParameters.Reason, CkMethodOperationReasons.HandlerLost), ("a", "1"));

        CkMethodOperationParameterGraphType.Sorted(error.Parameters).Select(p => p.Key).Should().Equal("a", "reason");
    }
}

/// <summary>A clock the test moves forward (the store hides expired records by its clock).</summary>
internal sealed class SteppingTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}
