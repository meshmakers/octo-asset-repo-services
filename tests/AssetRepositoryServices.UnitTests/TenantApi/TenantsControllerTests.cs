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

        _controller = new TenantsController(
            _octoService,
            A.Fake<IDistributionEventHubService>(),
            _tenantLifecycleStore,
            A.Fake<ITenantSetupRetryStore>(),
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
