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
using Meshmakers.Octo.Services.Infrastructure.Services;
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
    private readonly ITenantCapabilityStateReader _capabilityStateReader;
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

        // Default: no capability enabled, so the guard (AB#4255) lets every existing scenario through.
        _capabilityStateReader = A.Fake<ITenantCapabilityStateReader>();
        A.CallTo(() => _capabilityStateReader.GetEnabledCapabilitiesAsync(A<ITenantContext>._, A<string>._))
            .Returns(Array.Empty<TenantCapability>());

        _controller = new TenantsController(
            _octoService,
            A.Fake<IDistributionEventHubService>(),
            _tenantLifecycleStore,
            _tenantSetupRetryStore,
            A.Fake<ILogger<TenantsController>>(),
            _capabilityStateReader);

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

    /// <summary>
    ///     An own, active child tenant whose capability flags read as <paramref name="enabled" />.
    /// </summary>
    private void SetupOwnChild(string childTenantId, params TenantCapability[] enabled)
    {
        A.CallTo(() => _tenantContext.IsChildTenantExistingAsync(A<IOctoAdminSession>._, childTenantId))
            .Returns(true);
        A.CallTo(() => _tenantLifecycleStore.GetAsync(childTenantId, A<CancellationToken>._))
            .Returns((TenantLifecycleRecord?)null);
        A.CallTo(() => _capabilityStateReader.GetEnabledCapabilitiesAsync(_tenantContext, childTenantId))
            .Returns(enabled);
    }

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

    // --- Capability guard on delete / detach (AB#4255) ---

    [Fact]
    public async Task Delete_ReturnsConflict_WhenAChildCapabilityIsStillEnabled()
    {
        // Communication and Reporting own adapters/pools and report storage outside the tenant
        // database; a plain metadata delete would orphan them. The reply names exactly the enabled
        // capabilities, their disable verbs and the Dump-first advice.
        const string childTenantId = "child-a";
        SetupOwnChild(childTenantId, TenantCapability.Communication, TenantCapability.Reporting);

        var result = await _controller.Delete(childTenantId);

        var error = result.Should().BeOfType<ConflictObjectResult>().Subject
            .Value.Should().BeOfType<OperationFailedErrorDto>().Subject;
        error.Message.Should().Contain("cannot be deleted")
            .And.Contain("Communication, Reporting")
            .And.Contain("DisableCommunication, DisableReporting")
            .And.Contain("Dump")
            .And.Contain("Tenant Features")
            .And.NotContain("Stream Data");
        A.CallTo(() => _tenantLifecycleStore.MarkDeletingAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _tenantContext.DeleteChildTenantMetadataAsync(A<IOctoAdminSession>._, A<string>._))
            .MustNotHaveHappened();
        A.CallTo(() => _tenantContext.DropTenantDatabaseAsync(A<TenantDeletionHandle>._, A<string>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Delete_ReturnsConflict_NamingEveryEnabledCapability_InFixedOrder()
    {
        const string childTenantId = "child-a";
        SetupOwnChild(childTenantId, TenantCapability.StreamData, TenantCapability.Communication,
            TenantCapability.Reporting, TenantCapability.AiServices);

        var result = await _controller.Delete(childTenantId);

        var error = result.Should().BeOfType<ConflictObjectResult>().Subject
            .Value.Should().BeOfType<OperationFailedErrorDto>().Subject;
        error.Message.Should().Contain("Stream Data, Communication, Reporting, AI Services")
            .And.Contain("DisableStreamData, DisableCommunication, DisableReporting, DisableAi");
    }

    [Fact]
    public async Task Delete_NamesAiServicesWithoutStudioHint_WhenOnlyThatFlagIsLeft()
    {
        // The Studio's Tenant Features panel has no AI toggle, so pointing there would be a dead end.
        const string childTenantId = "child-a";
        SetupOwnChild(childTenantId, TenantCapability.AiServices);

        var result = await _controller.Delete(childTenantId);

        var error = result.Should().BeOfType<ConflictObjectResult>().Subject
            .Value.Should().BeOfType<OperationFailedErrorDto>().Subject;
        error.Message.Should().Contain("AI Services").And.Contain("DisableAi").And.NotContain("Tenant Features");
    }

    [Fact]
    public async Task Delete_LeavesNoTombstone_WhenRefusedForEnabledCapabilities()
    {
        // A refused delete must not tombstone the tenant id: the settle window would block the id
        // (and the sweep would arbitrate a delete that never happened) for ~2 min (AB#4829).
        const string childTenantId = "child-a";
        SetupOwnChild(childTenantId, TenantCapability.StreamData);

        await _controller.Delete(childTenantId);

        A.CallTo(() => _tenantLifecycleStore.MarkDeletingAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _tenantLifecycleStore.EnsureDeletingAsync(A<string>._, A<string?>._, A<Guid>._,
                A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Delete_DoesNotReadCapabilities_ForATenantOutsideTheOwnSubtree()
    {
        // The read resolves the child and opens its database - for a foreign tenant that would be an
        // existence oracle, so it must stay behind the ownership probe (AB#4763).
        const string foreignTenantId = "somebody-elses-tenant";
        A.CallTo(() => _tenantContext.IsChildTenantExistingAsync(A<IOctoAdminSession>._, foreignTenantId))
            .Returns(false);

        var result = await _controller.Delete(foreignTenantId);

        result.Should().BeOfType<NotFoundResult>();
        A.CallTo(() => _capabilityStateReader.GetEnabledCapabilitiesAsync(A<ITenantContext>._, A<string>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Delete_DoesNotReadCapabilities_WhileTheChildIsStillBeingCreated()
    {
        // Resolving a half-built tenant runs the CK auto-imports on it; the Creating guard answers first.
        const string childTenantId = "child-a";
        A.CallTo(() => _tenantContext.IsChildTenantExistingAsync(A<IOctoAdminSession>._, childTenantId))
            .Returns(true);
        A.CallTo(() => _tenantLifecycleStore.GetAsync(childTenantId, A<CancellationToken>._))
            .Returns(new TenantLifecycleRecord { TenantId = childTenantId, State = TenantLifecycleState.Creating });

        var result = await _controller.Delete(childTenantId);

        result.Should().BeOfType<ConflictObjectResult>().Subject
            .Value.Should().BeOfType<OperationFailedErrorDto>().Subject
            .Message.Should().Contain("still being created");
        A.CallTo(() => _capabilityStateReader.GetEnabledCapabilitiesAsync(A<ITenantContext>._, A<string>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Delete_Returns500AndWritesNoTombstone_WhenTheCapabilityStateCannotBeRead()
    {
        // An unreadable state is never "disabled": the delete does not proceed, and nothing is
        // tombstoned because nothing was deleted.
        const string childTenantId = "child-a";
        SetupOwnChild(childTenantId);
        A.CallTo(() => _capabilityStateReader.GetEnabledCapabilitiesAsync(_tenantContext, childTenantId))
            .Throws(new InvalidOperationException("mongo down"));

        var result = await _controller.Delete(childTenantId);

        result.Should().BeOfType<ObjectResult>().Subject.StatusCode.Should().Be(500);
        A.CallTo(() => _tenantLifecycleStore.MarkDeletingAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _tenantContext.DeleteChildTenantMetadataAsync(A<IOctoAdminSession>._, A<string>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_WhenTheChildVanishesBeforeTheCapabilityRead()
    {
        // Concurrent delete between the ownership probe and the read: the reader throws the engine's
        // not-found, which the existing TenantException branch maps to 404 - no tombstone written.
        const string childTenantId = "child-a";
        SetupOwnChild(childTenantId);
        A.CallTo(() => _capabilityStateReader.GetEnabledCapabilitiesAsync(_tenantContext, childTenantId))
            .Throws(TenantException.TenantDoesNotExist(childTenantId));

        var result = await _controller.Delete(childTenantId);

        result.Should().BeOfType<NotFoundObjectResult>();
        A.CallTo(() => _tenantLifecycleStore.MarkDeletingAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Detach_ReturnsNotFound_ForATenantOutsideTheOwnSubtree()
    {
        // Detach used to let the engine answer a 400 naming the tenant; it now shares Delete's
        // reason-free 404 (AB#4763) and never reads a foreign tenant's capabilities.
        const string foreignTenantId = "somebody-elses-tenant";
        A.CallTo(() => _tenantContext.IsChildTenantExistingAsync(A<IOctoAdminSession>._, foreignTenantId))
            .Returns(false);

        var result = await _controller.Detach(foreignTenantId);

        result.Should().BeOfType<NotFoundResult>();
        A.CallTo(() => _tenantContext.DetachChildTenantAsync(A<IOctoAdminSession>._, A<string>._))
            .MustNotHaveHappened();
        A.CallTo(() => _capabilityStateReader.GetEnabledCapabilitiesAsync(A<ITenantContext>._, A<string>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Detach_ReturnsConflict_WhenAChildCapabilityIsStillEnabled()
    {
        // A detached tenant keeps its database but loses its registry entry - the archives it still
        // owns would be orphaned exactly as on delete.
        const string childTenantId = "child-a";
        SetupOwnChild(childTenantId, TenantCapability.StreamData);

        var result = await _controller.Detach(childTenantId);

        var error = result.Should().BeOfType<ConflictObjectResult>().Subject
            .Value.Should().BeOfType<OperationFailedErrorDto>().Subject;
        error.Message.Should().Contain("cannot be detached")
            .And.Contain("Stream Data")
            .And.Contain("DisableStreamData")
            .And.Contain("Dump");
        A.CallTo(() => _tenantContext.DetachChildTenantAsync(A<IOctoAdminSession>._, A<string>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Detach_ReturnsNoContent_WhenAllCapabilitiesAreDisabled()
    {
        const string childTenantId = "child-a";
        SetupOwnChild(childTenantId);

        var result = await _controller.Detach(childTenantId);

        result.Should().BeOfType<NoContentResult>();
        A.CallTo(() => _tenantContext.DetachChildTenantAsync(A<IOctoAdminSession>._, childTenantId))
            .MustHaveHappened();
    }

    [Fact]
    public async Task Detach_MapsConflictTo409_LikeAttach()
    {
        // TenantException derives from PersistenceException, so the 400 branch used to swallow it.
        const string childTenantId = "child-a";
        SetupOwnChild(childTenantId);
        A.CallTo(() => _tenantContext.DetachChildTenantAsync(A<IOctoAdminSession>._, childTenantId))
            .Throws(TenantException.TenantIdNotAvailable(childTenantId));

        var result = await _controller.Detach(childTenantId);

        result.Should().BeOfType<ConflictObjectResult>().Subject
            .Value.Should().BeOfType<OperationFailedErrorDto>();
    }

    [Fact]
    public async Task Detach_MapsTenantNotFoundTo404_WhenTheChildVanishesMidFlight()
    {
        const string childTenantId = "child-a";
        SetupOwnChild(childTenantId);
        A.CallTo(() => _tenantContext.DetachChildTenantAsync(A<IOctoAdminSession>._, childTenantId))
            .Throws(TenantException.TenantDoesNotExist(childTenantId));

        var result = await _controller.Detach(childTenantId);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Detach_MapsPersistenceFailureTo400()
    {
        // A flag-less TenantException (neither conflict nor not-found) is a plain persistence failure.
        const string childTenantId = "child-a";
        SetupOwnChild(childTenantId);
        A.CallTo(() => _tenantContext.DetachChildTenantAsync(A<IOctoAdminSession>._, childTenantId))
            .Throws(TenantException.TenantDatabaseDoesNotExist("child-a-db"));

        var result = await _controller.Detach(childTenantId);

        result.Should().BeOfType<BadRequestObjectResult>().Subject
            .Value.Should().BeOfType<OperationFailedErrorDto>();
    }

    [Fact]
    public void BuildCapabilityConflictMessage_IsTheOperatorContract()
    {
        // The CLI prints this string raw, so wording, order and the context-based guidance (the
        // octo-cli disable verbs take no tenant argument) are part of the contract.
        var message = TenantsController.BuildCapabilityConflictMessage("child-a", "deleted",
            [TenantCapability.Communication, TenantCapability.Reporting]);

        message.Should().Be(
            "Tenant 'child-a' cannot be deleted while the following capabilities are still enabled: " +
            "Communication, Reporting. Disable them on tenant 'child-a' first: run DisableCommunication, " +
            "DisableReporting with octo-cli in a context of that tenant (UseContext or --context), or use " +
            "Refinery Studio (General > Settings > Tenant Features) of tenant 'child-a'. " +
            "If the tenant's data is still needed, create a backup with Dump before disabling.");
        message.Should().NotContain("-tid");
        message.Should().Match(m => m.All(c => c < 128), "the CLI prints the raw JSON body");
    }
}
