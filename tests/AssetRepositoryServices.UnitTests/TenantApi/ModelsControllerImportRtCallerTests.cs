using System.Security.Claims;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AssetRepositoryServices.UnitTests.TenantApi;

/// <summary>
///     AB#6392 — the ImportRt route hands the file to a bus command; the initiating caller travels with it so the
///     consumer can apply the blueprint-lock protection on their behalf. A caller without an identity is rejected,
///     never imported as if it were the system.
/// </summary>
public class ModelsControllerImportRtCallerTests
{
    private readonly ICommandClient<ImportRtCommandRequest> _importRtCommandClient =
        A.Fake<ICommandClient<ImportRtCommandRequest>>();

    private readonly IDistributedCacheService _distributedCache = A.Fake<IDistributedCacheService>();
    private readonly ModelsController _controller;

    public ModelsControllerImportRtCallerTests()
    {
        _controller = new ModelsController(
            _distributedCache,
            A.Fake<ICommandClient<ExportRtByQueryCommandRequest>>(),
            A.Fake<ICommandClient<ExportRtByDeepGraphCommandRequest>>(),
            _importRtCommandClient,
            A.Fake<ICommandClient<ImportCkCommandRequest>>(),
            A.Fake<ICommandClient<ImportCkBatchCommandRequest>>(),
            A.Fake<ICatalogService>(),
            A.Fake<ICkJsonSerializer>(),
            A.Fake<Meshmakers.Octo.Runtime.Contracts.MongoDb.ISystemContext>(),
            A.Fake<Meshmakers.Octo.Runtime.Contracts.CkModelMigrations.ICkModelUpgradeService>(),
            A.Fake<Meshmakers.Octo.Runtime.Contracts.CkModelMigrations.ICkModelMigrationService>(),
            A.Fake<ICkModelLibraryStatusService>());

        A.CallTo(() => _importRtCommandClient.GetResponse<JobCreatedResponse>(A<ImportRtCommandRequest>._))
            .Returns(new JobCreatedResponse("job-1"));
    }

    private void SignIn(params Claim[] claims)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(claims.Length == 0 ? new ClaimsIdentity() : new ClaimsIdentity(claims, "test"))
        };
        httpContext.Request.RouteValues["tenantId"] = "test-tenant";
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    private Task<IActionResult> ImportAsync()
    {
        return _controller.ImportRt(ImportStrategyDto.Upsert, A.Fake<IFormFile>());
    }

    [Fact]
    public async Task ImportRt_PassesTheInitiatingSubjectToTheCommand()
    {
        SignIn(new Claim("sub", "user-sub-1"));

        var result = await ImportAsync();

        result.Should().BeOfType<OkObjectResult>();
        A.CallTo(() => _importRtCommandClient.GetResponse<JobCreatedResponse>(
                A<ImportRtCommandRequest>.That.Matches(r =>
                    r.TenantId == "test-tenant" && r.ImportStrategy == ImportStrategy.Upsert &&
                    r.InitiatedBySubjectId == "user-sub-1")))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ImportRt_ClientCredentialsToken_UsesTheClientIdAsSubject()
    {
        SignIn(new Claim("client_id", "pipeline-client"));

        await ImportAsync();

        A.CallTo(() => _importRtCommandClient.GetResponse<JobCreatedResponse>(
                A<ImportRtCommandRequest>.That.Matches(r => r.InitiatedBySubjectId == "pipeline-client")))
            .MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData(false)] // not authenticated
    [InlineData(true)] // authenticated, but neither sub nor client_id
    public async Task ImportRt_CallerWithoutIdentity_IsRejected_AndNothingIsCachedOrSent(bool authenticated)
    {
        if (authenticated)
        {
            SignIn(new Claim("scope", "assets"));
        }
        else
        {
            SignIn();
        }

        var result = await ImportAsync();

        var unauthorized = result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        unauthorized.Value.Should().BeOfType<OperationFailedErrorDto>();
        A.CallTo(() => _importRtCommandClient.GetResponse<JobCreatedResponse>(A<ImportRtCommandRequest>._))
            .MustNotHaveHappened();
        A.CallTo(_distributedCache).MustNotHaveHappened();
    }
}
