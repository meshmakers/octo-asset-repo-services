using System.Security.Claims;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantOwnership;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AssetRepositoryServices.UnitTests.SystemApi;

/// <summary>
///     AB#8796: the ownership review endpoint is system-level only (403 otherwise, nothing is run), listing is
///     the default, stamp is passed through, the report carries the listed databases.
/// </summary>
public class TenantOwnershipReviewControllerTests
{
    private const string SystemTenantId = "octosystem";

    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly TenantRegistryController _controller;

    public TenantOwnershipReviewControllerTests()
    {
        A.CallTo(() => _systemContext.TenantId).Returns(SystemTenantId);
        A.CallTo(() => _systemContext.GetAdminSessionAsync()).Returns(A.Fake<IOctoAdminSession>());
        A.CallTo(() => _systemContext.ReviewTenantOwnershipAsync(A<IOctoAdminSession>._, A<bool>._))
            .ReturnsLazily(call => Task.FromResult(new TenantOwnershipReviewResult
            {
                SystemDatabaseName = "octosystem",
                Stamp = call.GetArgument<bool>(1),
                Checked = 4,
                Owned = 2,
                Entries =
                [
                    new TenantOwnershipReviewEntry("shared", "shared", TenantOwnershipIssue.RegisteredByOtherInstance,
                        "octosystemdev", ["octosystem", "octosystemdev"], false)
                ]
            }));
        _controller = new TenantRegistryController(_systemContext, NullLogger<TenantRegistryController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        As(User(SystemTenantId, CommonConstants.AdminPanelManagementRole));
    }

    private void As(ClaimsPrincipal user) => _controller.ControllerContext.HttpContext!.User = user;

    private static ClaimsPrincipal User(string tenantId, params string[] roles)
    {
        var claims = new List<Claim> { new("tenant_id", tenantId), new("sub", "admin") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public async Task ReviewOwnership_IsForbidden_OutsideTheSystemTenant_AndRunsNothing()
    {
        As(User("customer", CommonConstants.AdminPanelManagementRole));

        var result = await _controller.ReviewOwnership(stamp: true);

        result.Should().BeOfType<ForbidResult>();
        A.CallTo(() => _systemContext.ReviewTenantOwnershipAsync(A<IOctoAdminSession>._, A<bool>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ReviewOwnership_ListsByDefault_AndReturnsTheEntries()
    {
        var result = await _controller.ReviewOwnership();

        A.CallTo(() => _systemContext.ReviewTenantOwnershipAsync(A<IOctoAdminSession>._, false))
            .MustHaveHappenedOnceExactly();
        var report = result.Should().BeOfType<OkObjectResult>().Which.Value.Should()
            .BeOfType<TenantOwnershipReviewReportDto>().Subject;
        report.Stamp.Should().BeFalse();
        report.Checked.Should().Be(4);
        report.Owned.Should().Be(2);
        var entry = report.Entries.Should().ContainSingle().Subject;
        entry.Issue.Should().Be("RegisteredByOtherInstance");
        entry.MarkerOwnerSystemDatabaseName.Should().Be("octosystemdev");
        entry.RegisteringSystemDatabases.Should().Equal("octosystem", "octosystemdev");
        report.Summary.Should().StartWith("List");
    }

    [Fact]
    public async Task ReviewOwnership_PassesStampThrough()
    {
        var result = await _controller.ReviewOwnership(stamp: true);

        A.CallTo(() => _systemContext.ReviewTenantOwnershipAsync(A<IOctoAdminSession>._, true))
            .MustHaveHappenedOnceExactly();
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should()
            .BeOfType<TenantOwnershipReviewReportDto>().Which.Stamp.Should().BeTrue();
    }

    [Fact]
    public async Task ReviewOwnership_AnswersWithoutDetails_WhenTheReviewFails()
    {
        A.CallTo(() => _systemContext.ReviewTenantOwnershipAsync(A<IOctoAdminSession>._, A<bool>._))
            .Throws(new InvalidOperationException("secret detail"));

        var result = await _controller.ReviewOwnership(stamp: true);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(500);
        var error = status.Value.Should().BeOfType<InternalServerErrorDto>().Subject;
        error.Message.Should().NotBeNullOrWhiteSpace();
        error.Message.Should().NotContain("secret detail");
    }
}
