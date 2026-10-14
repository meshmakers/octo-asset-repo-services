using System.Security.Claims;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.OrphanSweep;
using Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AssetRepositoryServices.UnitTests.SystemApi;

/// <summary>
///     AB#6541: the orphan sweep endpoint is system-level only (403 otherwise, nothing is run), dry run is the
///     default, the grace period is passed through and never below the engine's minimum.
/// </summary>
public class FilesOrphanSweepControllerTests
{
    private const string SystemTenantId = "octosystem";
    private const string Tenant = "customer";

    private readonly IGridFsOrphanSweepService _service = A.Fake<IGridFsOrphanSweepService>();
    private readonly FilesOrphanSweepController _controller;

    public FilesOrphanSweepControllerTests()
    {
        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.TenantId).Returns(SystemTenantId);
        _controller = new FilesOrphanSweepController(_service, systemContext,
            NullLogger<FilesOrphanSweepController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        As(User(SystemTenantId, "admin", CommonConstants.AdminPanelManagementRole));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void As(ClaimsPrincipal user) => _controller.ControllerContext.HttpContext!.User = user;

    private static ClaimsPrincipal User(string? tenantId, string? subject, params string[] roles)
    {
        var claims = new List<Claim>();
        if (tenantId != null)
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }

        if (subject != null)
        {
            claims.Add(new Claim("sub", subject));
        }

        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static GridFsOrphanSweepReportDto Report(bool removed = false) => new()
    {
        TenantId = Tenant,
        Mode = removed ? "remove" : "dry-run",
        GracePeriodMinutes = 1440,
        ExaminedFiles = 10,
        ReferencedFiles = 8,
        YoungFiles = 3,
        OrphanFiles = 2,
        OrphanChunks = 4,
        RemovedFiles = removed ? 2 : 0,
        OrphanBytes = 2048,
        DurationMilliseconds = 12
    };

    public static TheoryData<string?, string?, string[]> UnauthorizedCallers => new()
    {
        { null, "user", [CommonConstants.AdminPanelManagementRole] },          // no tenant claim: fail closed
        { "customer", "user", [CommonConstants.AdminPanelManagementRole] },    // admin of another tenant
        { SystemTenantId, "user", [] },                                        // system tenant, no admin role
        { SystemTenantId, "user", ["FileManagement"] },                        // some other role
    };

    [Theory]
    [MemberData(nameof(UnauthorizedCallers))]
    public async Task UnauthorizedCaller_Gets403_AndNothingRuns(string? tenantId, string? subject, string[] roles)
    {
        As(User(tenantId, subject, roles));

        var result = await _controller.SweepOrphans(Tenant, remove: true, cancellationToken: Ct);

        result.Should().BeOfType<ForbidResult>();
        A.CallTo(_service).MustNotHaveHappened();
    }

    [Fact]
    public async Task ServiceToken_OfTheSystemTenant_IsAllowed()
    {
        As(User(SystemTenantId, subject: null));
        A.CallTo(() => _service.SweepAsync(Tenant, false, A<TimeSpan>._, A<CancellationToken>._)).Returns(Report());

        (await _controller.SweepOrphans(Tenant, cancellationToken: Ct)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task WithoutParameters_IsADryRun_WithTheDefaultGracePeriod_AndReturnsTheReport()
    {
        A.CallTo(() => _service.SweepAsync(Tenant, false, TimeSpan.FromHours(24), A<CancellationToken>._))
            .Returns(Report());

        var result = await _controller.SweepOrphans(Tenant, cancellationToken: Ct);

        var report = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<GridFsOrphanSweepReportDto>().Subject;
        report.Mode.Should().Be("dry-run");
        report.RemovedFiles.Should().Be(0);
        A.CallTo(() => _service.SweepAsync(Tenant, true, A<TimeSpan>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Remove_AndGracePeriod_ArePassedThrough()
    {
        A.CallTo(() => _service.SweepAsync(Tenant, true, TimeSpan.FromMinutes(90), A<CancellationToken>._))
            .Returns(Report(removed: true));

        var result = await _controller.SweepOrphans(Tenant, remove: true, gracePeriodMinutes: 90, cancellationToken: Ct);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<GridFsOrphanSweepReportDto>()
            .Which.Mode.Should().Be("remove");
    }

    [Theory]
    [InlineData(9.99)]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.MaxValue)]
    [InlineData(double.PositiveInfinity)]
    public async Task GracePeriodBelowTheEngineMinimum_Gets400_AndNothingRuns(double minutes)
    {
        var result = await _controller.SweepOrphans(Tenant, remove: true, gracePeriodMinutes: minutes, cancellationToken: Ct);

        result.Should().BeOfType<BadRequestObjectResult>();
        A.CallTo(_service).MustNotHaveHappened();
    }

    [Fact]
    public async Task GracePeriodExactlyTheMinimum_IsAccepted()
    {
        A.CallTo(() => _service.SweepAsync(Tenant, false, GridFsOrphanSweepOptions.MinimumGracePeriod, A<CancellationToken>._))
            .Returns(Report());

        (await _controller.SweepOrphans(Tenant, gracePeriodMinutes: 10, cancellationToken: Ct))
            .Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task UnknownTenant_Gets404()
    {
        A.CallTo(() => _service.SweepAsync(A<string>._, A<bool>._, A<TimeSpan>._, A<CancellationToken>._))
            .Returns((GridFsOrphanSweepReportDto?)null);

        (await _controller.SweepOrphans("nope", cancellationToken: Ct)).Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task SweepAlreadyRunning_Gets409()
    {
        A.CallTo(() => _service.SweepAsync(A<string>._, A<bool>._, A<TimeSpan>._, A<CancellationToken>._))
            .Throws(new GridFsOrphanSweepBusyException(Tenant));

        (await _controller.SweepOrphans(Tenant, cancellationToken: Ct)).Should().BeOfType<ConflictResult>();
    }

    [Fact]
    public async Task Failure_Gets500_WithoutLeakingTheException()
    {
        A.CallTo(() => _service.SweepAsync(A<string>._, A<bool>._, A<TimeSpan>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("mongodb://admin:secret@host"));

        var result = await _controller.SweepOrphans(Tenant, cancellationToken: Ct);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        status.Value!.ToString().Should().NotContain("secret");
    }
}
