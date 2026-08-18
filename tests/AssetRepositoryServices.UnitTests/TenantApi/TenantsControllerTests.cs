using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AssetRepositoryServices.UnitTests.TenantApi;

public class TenantsControllerTests
{
    private const string OwnTenantId = "maco";
    private const string OwnDatabase = "maco_db";

    private readonly IOctoService _octoService;
    private readonly ISystemContext _systemContext;
    private readonly ITenantContext _tenantContext;
    private readonly ITenantLifecycleStore _tenantLifecycleStore;
    private readonly ITenantSetupRetryStore _tenantSetupRetryStore;
    private readonly TenantsController _controller;

    public TenantsControllerTests()
    {
        _octoService = A.Fake<IOctoService>();
        _systemContext = A.Fake<ISystemContext>();
        _tenantContext = A.Fake<ITenantContext>();

        A.CallTo(() => _octoService.SystemContext).Returns(_systemContext);
        A.CallTo(() => _systemContext.TryFindTenantContextAsync(OwnTenantId)).Returns(_tenantContext);
        A.CallTo(() => _tenantContext.TenantId).Returns(OwnTenantId);
        A.CallTo(() => _tenantContext.DatabaseName).Returns(OwnDatabase);
        A.CallTo(() => _tenantContext.GetAdminSessionAsync()).Returns(A.Fake<IOctoAdminSession>());

        _tenantLifecycleStore = A.Fake<ITenantLifecycleStore>();
        _tenantSetupRetryStore = A.Fake<ITenantSetupRetryStore>();

        _controller = new TenantsController(
            _octoService,
            A.Fake<IDistributionEventHubService>(),
            _tenantLifecycleStore,
            _tenantSetupRetryStore,
            A.Fake<ILogger<TenantsController>>());

        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = OwnTenantId;
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    private void SetupChildTenants(long totalCount, params OctoTenant[] items)
    {
        var resultSet = A.Fake<IResultSet<OctoTenant>>();
        A.CallTo(() => resultSet.Items).Returns(items);
        A.CallTo(() => resultSet.TotalCount).Returns(totalCount);
        A.CallTo(() => _tenantContext.GetChildTenantsAsync(A<IOctoAdminSession>._, A<int?>._, A<int?>._))
            .Returns(resultSet);
    }

    [Fact]
    public async Task Get_ReturnsEmptyPage_WhenTenantHasNoChildren()
    {
        SetupChildTenants(0);

        var paged = await GetPaged(new PagingParams { Skip = 0, Take = 100 });

        paged.List.Should().BeEmpty();
        paged.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Get_ReturnsOnlyChildTenants_AndNeverTheOwnTenant()
    {
        SetupChildTenants(2,
            new OctoTenant("child-a", "child-a-db"),
            new OctoTenant("child-b", "child-b-db"));

        var paged = await GetPaged(new PagingParams { Skip = 0, Take = 100 });

        paged.List.Select(t => t.TenantId).Should().Equal("child-a", "child-b");
        paged.List.Select(t => t.TenantId).Should().NotContain(OwnTenantId);
        paged.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task Get_PassesPagingThroughUnchanged()
    {
        SetupChildTenants(20, new OctoTenant("child-a", "child-a-db"));

        await GetPaged(new PagingParams { Skip = 5, Take = 10 });

        A.CallTo(() => _tenantContext.GetChildTenantsAsync(A<IOctoAdminSession>._, 5, 10))
            .MustHaveHappened();
    }

    [Fact]
    public async Task Get_ReturnsAllChildren_WhenNotPaged()
    {
        SetupChildTenants(2,
            new OctoTenant("child-a", "child-a-db"),
            new OctoTenant("child-b", "child-b-db"));

        var result = await _controller.Get((PagingParams?)null);

        var list = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<IEnumerable<TenantDto>>().Subject.ToList();
        list.Select(t => t.TenantId).Should().Equal("child-a", "child-b");
    }

    [Fact]
    public async Task GetSelf_ReturnsCurrentTenantWithItsDatabaseName()
    {
        var result = await _controller.GetSelf();

        result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<TenantDto>().Subject
            .Should().Match<TenantDto>(t => t.TenantId == OwnTenantId && t.Database == OwnDatabase);
    }

    [Fact]
    public async Task GetSelf_DoesNotQueryTheChildTenantRegistry()
    {
        SetupChildTenants(1, new OctoTenant("child-a", "child-a-db"));

        await _controller.GetSelf();

        A.CallTo(() => _tenantContext.GetChildTenantsAsync(A<IOctoAdminSession>._, A<int?>._, A<int?>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task GetSelf_ReturnsBadRequest_WhenRouteCarriesNoTenant()
    {
        _controller.ControllerContext.HttpContext.Request.RouteValues.Remove("tenantId");

        var result = await _controller.GetSelf();

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    private async Task<PagedResult<TenantDto>> GetPaged(PagingParams pagingParams)
    {
        var result = await _controller.Get(pagingParams);
        return result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<PagedResult<TenantDto>>().Subject;
    }

    // --- Namespace conflicts and lifecycle disclosure (AB#4762 / AB#4763) ---

    private static string GenericTenantIdConflict(string tenantId) =>
        TenantException.TenantIdNotAvailable(tenantId).Message;

    [Fact]
    public async Task Post_ReturnsGenericConflict_WhenTenantIsBeingDeleted()
    {
        const string childTenantId = "child-a";
        A.CallTo(() => _tenantLifecycleStore.GetAsync(childTenantId, A<CancellationToken>._))
            .Returns(new TenantLifecycleRecord { TenantId = childTenantId, State = TenantLifecycleState.Deleting });

        var result = await _controller.Post(childTenantId, "child-a-db");

        // Must be indistinguishable from "the id is taken by a tenant you cannot see": the lifecycle
        // store is platform-global, so a distinguishable answer would be an existence oracle.
        var error = result.Should().BeOfType<ConflictObjectResult>().Subject
            .Value.Should().BeOfType<OperationFailedErrorDto>().Subject;
        error.Message.Should().Be(GenericTenantIdConflict(childTenantId));
        error.Message.Should().NotContain("deletion");
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_ForATenantOutsideTheOwnSubtree()
    {
        const string foreignTenantId = "somebody-elses-tenant";
        A.CallTo(() => _tenantContext.IsChildTenantExistingAsync(A<IOctoAdminSession>._, foreignTenantId))
            .Returns(false);

        var result = await _controller.Delete(foreignTenantId);

        result.Should().BeOfType<NotFoundResult>();

        // Ownership must be settled before the platform-global lifecycle store is consulted, otherwise
        // the reply exposes the provisioning state of a tenant the caller cannot see (AB#4763).
        A.CallTo(() => _tenantLifecycleStore.GetAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Delete_ReturnsConflict_WhenAnOwnChildIsStillBeingCreated()
    {
        const string childTenantId = "child-a";
        A.CallTo(() => _tenantContext.IsChildTenantExistingAsync(A<IOctoAdminSession>._, childTenantId))
            .Returns(true);
        A.CallTo(() => _tenantLifecycleStore.GetAsync(childTenantId, A<CancellationToken>._))
            .Returns(new TenantLifecycleRecord { TenantId = childTenantId, State = TenantLifecycleState.Creating });

        var result = await _controller.Delete(childTenantId);

        // The caller owns this tenant, so the reason is safe to state — and "already in use" would be
        // nonsense as the answer to a delete.
        var error = result.Should().BeOfType<ConflictObjectResult>().Subject
            .Value.Should().BeOfType<OperationFailedErrorDto>().Subject;
        error.Message.Should().Contain("still being created");
    }

    /// <summary>
    ///     AB#4829 review follow-up. Attach lacked the Deleting guard Post has: during the ~2 min
    ///     settle window an attach of the deleted tenant id succeeded and registered a tenant whose
    ///     live tombstone made every service's setup skip silently — with nothing ever requeueing
    ///     that setup once the sweep later removed the tombstone.
    /// </summary>
    [Fact]
    public async Task Attach_ReturnsGenericConflict_WhenTenantIsBeingDeleted()
    {
        const string childTenantId = "child-a";
        A.CallTo(() => _tenantLifecycleStore.GetAsync(childTenantId, A<CancellationToken>._))
            .Returns(new TenantLifecycleRecord { TenantId = childTenantId, State = TenantLifecycleState.Deleting });

        var result = await _controller.Attach(childTenantId, "child-a-db");

        var error = result.Should().BeOfType<ConflictObjectResult>().Subject
            .Value.Should().BeOfType<OperationFailedErrorDto>().Subject;
        error.Message.Should().Be(GenericTenantIdConflict(childTenantId));
        error.Message.Should().NotContain("deletion");
        A.CallTo(() => _tenantContext.AttachChildTenantAsync(A<IOctoAdminSession>._, A<string>._, A<string>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Attach_MapsConflictTo409_LikePost()
    {
        A.CallTo(() => _tenantContext.AttachChildTenantAsync(A<IOctoAdminSession>._, "child-a-db", "child-a"))
            .Throws(TenantException.DatabaseNameNotAvailable("child-a-db"));

        var result = await _controller.Attach("child-a", "child-a-db");

        // TenantException derives from PersistenceException, so without the dedicated branch this
        // identical condition answered 400 on attach and 409 on create.
        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Attach_MapsFormatValidationTo400_LikePost()
    {
        // The namespace gate throws ArgumentException for a format-invalid tenant id or database
        // name, before any conflict check. Attach must map it to 400 like Post — without its own
        // ArgumentException branch, the identical invalid input fell through to the generic
        // catch and answered 500.
        A.CallTo(() => _tenantContext.AttachChildTenantAsync(A<IOctoAdminSession>._, "bad$db", "child-a"))
            .Throws(new ArgumentException("Database name 'bad$db' is invalid."));

        var result = await _controller.Attach("child-a", "bad$db");

        result.Should().BeOfType<BadRequestObjectResult>().Subject
            .Value.Should().BeOfType<OperationFailedErrorDto>();
    }

    [Fact]
    public async Task Delete_LeavesTheSettleTombstone_ForTheSweep()
    {
        // AB#4829: events and setups already in flight can re-seed retry rows and resurrect the
        // dropped database for up to the settle period, and since AB#4762 nothing reclaims such a
        // shell. The delete therefore ends with an upserted Deleting tombstone (EnsureDeleting also
        // covers legacy tenants and records the database name the sweep needs to re-drop); the
        // reconciler's settle sweep completes the delete once the period has passed.
        const string childTenantId = "child-a";
        var correlationId = Guid.NewGuid();
        A.CallTo(() => _tenantContext.IsChildTenantExistingAsync(A<IOctoAdminSession>._, childTenantId))
            .Returns(true);
        A.CallTo(() => _tenantLifecycleStore.GetAsync(childTenantId, A<CancellationToken>._))
            .Returns((TenantLifecycleRecord?)null);
        A.CallTo(() => _tenantContext.DeleteChildTenantMetadataAsync(A<IOctoAdminSession>._, childTenantId))
            .Returns(new TenantDeletionHandle("child-a-db", correlationId));

        var result = await _controller.Delete(childTenantId);

        result.Should().BeOfType<OkResult>();
        A.CallTo(() => _tenantSetupRetryStore.ClearAllForTenantAsync(childTenantId, A<CancellationToken>._))
            .MustHaveHappened();
        A.CallTo(() => _tenantLifecycleStore.EnsureDeletingAsync(childTenantId, "child-a-db", correlationId,
                A<CancellationToken>._))
            .MustHaveHappened();
        A.CallTo(() => _tenantLifecycleStore.RemoveAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Delete_SucceedsEvenIfTheSettleTombstoneWriteFails()
    {
        // The metadata is committed and the database dropped at that point - the delete HAS happened.
        // MarkDeleting's tombstone still stands for the sweep; only the clock restamp / database name
        // is lost, which the sweep tolerates.
        const string childTenantId = "child-a";
        A.CallTo(() => _tenantContext.IsChildTenantExistingAsync(A<IOctoAdminSession>._, childTenantId))
            .Returns(true);
        A.CallTo(() => _tenantLifecycleStore.GetAsync(childTenantId, A<CancellationToken>._))
            .Returns((TenantLifecycleRecord?)null);
        A.CallTo(() => _tenantContext.DeleteChildTenantMetadataAsync(A<IOctoAdminSession>._, childTenantId))
            .Returns(new TenantDeletionHandle("child-a-db", Guid.NewGuid()));
        A.CallTo(() => _tenantLifecycleStore.EnsureDeletingAsync(A<string>._, A<string?>._, A<Guid>._,
                A<CancellationToken>._))
            .ThrowsAsync(new TimeoutException("store down"));

        var result = await _controller.Delete(childTenantId);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task Delete_KeepsTheTombstone_WhenTheDeleteFails()
    {
        // A failed delete leaves the tombstone for the settle sweep to arbitrate (AB#4829): tenant
        // still registered -> rollback; registry entry gone -> completion, including the re-drop of a
        // half-deleted database. Removing it here re-opened the tenant id while the tenant's state was
        // undefined - and when the drop had already happened, left an orphan that permanently blocked
        // its own database name.
        const string childTenantId = "child-a";
        A.CallTo(() => _tenantContext.IsChildTenantExistingAsync(A<IOctoAdminSession>._, childTenantId))
            .Returns(true);
        A.CallTo(() => _tenantLifecycleStore.GetAsync(childTenantId, A<CancellationToken>._))
            .Returns((TenantLifecycleRecord?)null);
        A.CallTo(() => _tenantContext.DeleteChildTenantMetadataAsync(A<IOctoAdminSession>._, childTenantId))
            .Throws(new InvalidOperationException("mongo down"));

        var result = await _controller.Delete(childTenantId);

        result.Should().BeOfType<ObjectResult>().Subject.StatusCode.Should().Be(500);
        A.CallTo(() => _tenantLifecycleStore.RemoveAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task GetLifecycle_ReturnsNotFound_ForATenantOutsideTheOwnSubtree()
    {
        const string foreignTenantId = "somebody-elses-tenant";
        A.CallTo(() => _tenantContext.IsChildTenantExistingAsync(A<IOctoAdminSession>._, foreignTenantId))
            .Returns(false);

        var result = await _controller.GetLifecycle(foreignTenantId);

        result.Should().BeOfType<NotFoundResult>();

        // Without this gate the generic conflict above is pointless: the caller would simply read the
        // colliding tenant's database name and last error from here (AB#4763).
        A.CallTo(() => _tenantLifecycleStore.GetAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task GetLifecycle_ReturnsRecord_ForAnOwnChildTenant()
    {
        const string childTenantId = "child-a";
        A.CallTo(() => _tenantContext.IsChildTenantExistingAsync(A<IOctoAdminSession>._, childTenantId))
            .Returns(true);
        A.CallTo(() => _tenantLifecycleStore.GetAsync(childTenantId, A<CancellationToken>._))
            .Returns(new TenantLifecycleRecord { TenantId = childTenantId, State = TenantLifecycleState.Active });

        var result = await _controller.GetLifecycle(childTenantId);

        result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<TenantLifecycleDto>().Subject
            .TenantId.Should().Be(childTenantId);
    }
}
