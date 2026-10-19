using System.Security.Claims;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AssetRepositoryServices.UnitTests.TenantApi;

/// <summary>
///     AB#6734 — the export routes hand the job to a bus command; the caller's identity (subject, roles, client)
///     travels with it, built with the mapping the GraphQL surface uses, so the job runs with the caller's data
///     permissions. A caller without an identity is rejected before a job is enqueued, never exported as the system.
/// </summary>
public class ModelsControllerExportCallerTests
{
    private readonly ICommandClient<ExportRtByQueryCommandRequest> _exportByQuery =
        A.Fake<ICommandClient<ExportRtByQueryCommandRequest>>();

    private readonly ICommandClient<ExportRtByDeepGraphCommandRequest> _exportByDeepGraph =
        A.Fake<ICommandClient<ExportRtByDeepGraphCommandRequest>>();

    private readonly ModelsController _controller;

    public ModelsControllerExportCallerTests()
    {
        _controller = new ModelsController(
            A.Fake<IDistributedCacheService>(),
            _exportByQuery,
            _exportByDeepGraph,
            A.Fake<ICommandClient<ImportRtCommandRequest>>(),
            A.Fake<ICommandClient<ImportCkCommandRequest>>(),
            A.Fake<ICommandClient<ImportCkBatchCommandRequest>>(),
            A.Fake<ICatalogService>(),
            A.Fake<ICkJsonSerializer>(),
            A.Fake<Meshmakers.Octo.Runtime.Contracts.MongoDb.ISystemContext>(),
            A.Fake<Meshmakers.Octo.Runtime.Contracts.CkModelMigrations.ICkModelUpgradeService>(),
            A.Fake<Meshmakers.Octo.Runtime.Contracts.CkModelMigrations.ICkModelMigrationService>(),
            A.Fake<ICkModelLibraryStatusService>(),
            A.Fake<ILogger<ModelsController>>(),
            A.Fake<ICkSerializer>(),
            A.Fake<Microsoft.AspNetCore.Authorization.IAuthorizationService>());

        A.CallTo(() => _exportByQuery.GetResponse<JobCreatedResponse>(A<ExportRtByQueryCommandRequest>._))
            .Returns(new JobCreatedResponse("job-q"));
        A.CallTo(() => _exportByDeepGraph.GetResponse<JobCreatedResponse>(A<ExportRtByDeepGraphCommandRequest>._))
            .Returns(new JobCreatedResponse("job-g"));
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

    private static ExportModelRequestByQueryDto QueryDto() => new() { QueryId = OctoObjectId.GenerateNewId() };

    private static ExportModelRequestByDeepGraphDto DeepGraphDto() =>
        new(new RtCkId<CkTypeId>("Test/Customer"), [OctoObjectId.GenerateNewId()]);

    [Fact]
    public async Task ExportByQuery_PassesSubjectRolesAndClientToTheCommand()
    {
        SignIn(new Claim("sub", "user-sub-1"), new Claim("role", "Reader"), new Claim(ClaimTypes.Role, "Analyst"),
            new Claim("client_id", "studio"));

        var result = await _controller.ExportRtByQueryAsync(QueryDto());

        result.Should().BeOfType<OkObjectResult>();
        A.CallTo(() => _exportByQuery.GetResponse<JobCreatedResponse>(
                A<ExportRtByQueryCommandRequest>.That.Matches(r =>
                    r.TenantId == "test-tenant" && r.InitiatedBySubjectId == "user-sub-1" &&
                    r.InitiatedByClientId == "studio" &&
                    r.InitiatedByRoleNames!.OrderBy(x => x).SequenceEqual(new[] { "Analyst", "Reader" }))))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExportByDeepGraph_PassesSubjectRolesAndClientToTheCommand()
    {
        SignIn(new Claim("sub", "user-sub-2"), new Claim("role", "Reader"), new Claim("client_id", "studio"));

        var result = await _controller.ExportRtByDeepGraphAsync(DeepGraphDto());

        result.Should().BeOfType<OkObjectResult>();
        A.CallTo(() => _exportByDeepGraph.GetResponse<JobCreatedResponse>(
                A<ExportRtByDeepGraphCommandRequest>.That.Matches(r =>
                    r.InitiatedBySubjectId == "user-sub-2" && r.InitiatedByClientId == "studio" &&
                    r.InitiatedByRoleNames!.SequenceEqual(new[] { "Reader" }))))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ClientCredentialsTokenWithoutSub_UsesTheClientIdAsSubject_NotTheSystem()
    {
        SignIn(new Claim("client_id", "pipeline-client"), new Claim("role", "Reader"));

        await _controller.ExportRtByQueryAsync(QueryDto());
        await _controller.ExportRtByDeepGraphAsync(DeepGraphDto());

        A.CallTo(() => _exportByQuery.GetResponse<JobCreatedResponse>(
                A<ExportRtByQueryCommandRequest>.That.Matches(r =>
                    r.InitiatedBySubjectId == "pipeline-client" && r.InitiatedByClientId == "pipeline-client" &&
                    r.InitiatedByRoleNames!.Single() == "Reader")))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _exportByDeepGraph.GetResponse<JobCreatedResponse>(
                A<ExportRtByDeepGraphCommandRequest>.That.Matches(r => r.InitiatedBySubjectId == "pipeline-client")))
            .MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData(false)] // not authenticated
    [InlineData(true)] // authenticated, but neither sub nor client_id
    public async Task CallerWithoutIdentity_IsRejected_AndNoJobIsEnqueued(bool authenticated)
    {
        if (authenticated)
        {
            SignIn(new Claim("scope", "assets"), new Claim("role", "Reader"));
        }
        else
        {
            SignIn();
        }

        var byQuery = await _controller.ExportRtByQueryAsync(QueryDto());
        var byDeepGraph = await _controller.ExportRtByDeepGraphAsync(DeepGraphDto());

        byQuery.Should().BeOfType<UnauthorizedObjectResult>().Which.Value.Should().BeOfType<OperationFailedErrorDto>();
        byDeepGraph.Should().BeOfType<UnauthorizedObjectResult>().Which.Value.Should().BeOfType<OperationFailedErrorDto>();
        A.CallTo(() => _exportByQuery.GetResponse<JobCreatedResponse>(A<ExportRtByQueryCommandRequest>._))
            .MustNotHaveHappened();
        A.CallTo(() => _exportByDeepGraph.GetResponse<JobCreatedResponse>(A<ExportRtByDeepGraphCommandRequest>._))
            .MustNotHaveHappened();
    }
}
