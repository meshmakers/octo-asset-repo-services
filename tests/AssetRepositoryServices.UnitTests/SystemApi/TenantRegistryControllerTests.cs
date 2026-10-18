using System.Security.Claims;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AssetRepositoryServices.UnitTests.SystemApi;

/// <summary>
///     AB#6971: the registry reconcile endpoint is system-level only (403 otherwise, nothing is run), dry run is the
///     default, apply is passed through, the report carries counts, inserts and conflicts.
/// </summary>
public class TenantRegistryControllerTests
{
    private const string SystemTenantId = "octosystem";

    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly TenantRegistryController _controller;

    public TenantRegistryControllerTests()
    {
        A.CallTo(() => _systemContext.TenantId).Returns(SystemTenantId);
        A.CallTo(() => _systemContext.GetAdminSessionAsync()).Returns(A.Fake<IOctoAdminSession>());
        A.CallTo(() => _systemContext.ReconcileTenantRegistryAsync(A<IOctoAdminSession>._, A<bool>._))
            .ReturnsLazily(call => Task.FromResult(new TenantRegistryReconcileResult
            {
                Applied = call.GetArgument<bool>(1),
                Visited = 5,
                AlreadyPresent = 3,
                Inserted = [new TenantRegistryReconcileEntry("ec_sub", "ec_sub", "ecm")],
                Conflicts = [new TenantRegistryReconcileConflict("ghost", "ecm", "database 'x' is registered for tenant 'y'")]
            }));
        _controller = new TenantRegistryController(_systemContext, NullLogger<TenantRegistryController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        As(User(SystemTenantId, "admin", CommonConstants.AdminPanelManagementRole));
    }

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

    public static TheoryData<string?, string?, string[]> UnauthorizedCallers => new()
    {
        { "customer", "admin", [CommonConstants.AdminPanelManagementRole] },
        { SystemTenantId, "admin", [] },
        { null, "admin", [CommonConstants.AdminPanelManagementRole] }
    };

    [Theory]
    [MemberData(nameof(UnauthorizedCallers))]
    public async Task ReconcileRegistry_IsForbidden_ForCallersOutsideTheSystemTenant_AndRunsNothing(
        string? tenantId, string? subject, string[] roles)
    {
        As(User(tenantId, subject, roles));

        var result = await _controller.ReconcileRegistry(apply: true);

        result.Should().BeOfType<ForbidResult>();
        A.CallTo(() => _systemContext.ReconcileTenantRegistryAsync(A<IOctoAdminSession>._, A<bool>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ReconcileRegistry_IsADryRunByDefault_AndReturnsCountsInsertsAndConflicts()
    {
        var result = await _controller.ReconcileRegistry();

        A.CallTo(() => _systemContext.ReconcileTenantRegistryAsync(A<IOctoAdminSession>._, false))
            .MustHaveHappenedOnceExactly();
        var report = result.Should().BeOfType<OkObjectResult>().Which.Value.Should()
            .BeOfType<TenantRegistryReconcileReportDto>().Subject;
        report.Applied.Should().BeFalse();
        report.Walked.Should().Be(5);
        report.AlreadyPresent.Should().Be(3);
        report.Inserted.Should().ContainSingle().Which.ParentTenantId.Should().Be("ecm");
        report.Conflicts.Should().ContainSingle().Which.Reason.Should().Contain("registered for tenant");
        report.Summary.Should().Contain("Dry run");
    }

    [Fact]
    public async Task ReconcileRegistry_PassesApplyThrough()
    {
        var result = await _controller.ReconcileRegistry(apply: true);

        A.CallTo(() => _systemContext.ReconcileTenantRegistryAsync(A<IOctoAdminSession>._, true))
            .MustHaveHappenedOnceExactly();
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should()
            .BeOfType<TenantRegistryReconcileReportDto>().Which.Applied.Should().BeTrue();
    }

    [Fact]
    public async Task ReconcileRegistry_AnswersWithoutDetails_WhenTheReconcileFails()
    {
        A.CallTo(() => _systemContext.ReconcileTenantRegistryAsync(A<IOctoAdminSession>._, A<bool>._))
            .Throws(new InvalidOperationException("secret detail"));

        var result = await _controller.ReconcileRegistry(apply: true);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(500);
        status.Value!.ToString().Should().NotContain("secret detail");
    }
}
