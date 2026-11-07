using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.CkModelValidation;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.CkModelMigrations;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AssetRepositoryServices.UnitTests.TenantApi;

/// <summary>
///     AB#8492: <c>POST Models/ValidateCk</c> hands the draft to the validator and answers 200 with its result, valid or
///     not; it caches nothing and queues no import job.
/// </summary>
public class ModelsControllerValidateCkTests
{
    private readonly ICkModelDraftValidator _validator = A.Fake<ICkModelDraftValidator>();
    private readonly ICommandClient<ImportCkCommandRequest> _importCk = A.Fake<ICommandClient<ImportCkCommandRequest>>();
    private readonly IDistributedCacheService _cache = A.Fake<IDistributedCacheService>();
    private readonly ModelsController _controller;

    public ModelsControllerValidateCkTests()
    {
        _controller = new ModelsController(_cache,
            A.Fake<ICommandClient<ExportRtByQueryCommandRequest>>(),
            A.Fake<ICommandClient<ExportRtByDeepGraphCommandRequest>>(),
            A.Fake<ICommandClient<ImportRtCommandRequest>>(),
            _importCk,
            A.Fake<ICommandClient<ImportCkBatchCommandRequest>>(),
            A.Fake<ICatalogService>(),
            A.Fake<ICkJsonSerializer>(),
            A.Fake<ISystemContext>(),
            A.Fake<ICkModelUpgradeService>(),
            A.Fake<ICkModelMigrationService>(),
            A.Fake<ICkModelLibraryStatusService>(),
            A.Fake<ILogger<ModelsController>>(),
            A.Fake<ICkSerializer>(),
            A.Fake<IAuthorizationService>(),
            _validator);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = "draftie";
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    private static IFormFile File(string name, string contentType, byte[] bytes) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name)
        {
            Headers = new HeaderDictionary(), ContentType = contentType
        };

    [Fact]
    public async Task ValidateCk_AnInvalidDraft_Is200WithTheErrors_AndNothingIsQueued()
    {
        var invalid = new CkModelValidationResponseDto
        {
            IsValid = false,
            Errors = [new CkModelValidationMessageDto { MessageNumber = 1, Level = "Error", Message = "m" }]
        };
        A.CallTo(() => _validator.ValidateAsync("draftie", A<Stream>._, "draft.zip", "application/zip",
            A<CancellationToken>._)).Returns(invalid);

        var result = await _controller.ValidateCk(File("draft.zip", "application/zip", [1, 2, 3]),
            TestContext.Current.CancellationToken);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(invalid);
        A.CallTo(_importCk).MustNotHaveHappened();
        A.CallTo(_cache).MustNotHaveHappened();
    }

    [Fact]
    public async Task ValidateCk_WithoutAFile_Is400_AndTheValidatorIsNotCalled()
    {
        var result = await _controller.ValidateCk(null, TestContext.Current.CancellationToken);

        result.Should().BeOfType<BadRequestObjectResult>();
        A.CallTo(_validator).MustNotHaveHappened();
    }

    [Fact]
    public void ValidateCk_NeedsOnlyTheReadOnlyScope()
    {
        var method = typeof(ModelsController).GetMethod(nameof(ModelsController.ValidateCk))!;
        var policies = method.GetCustomAttributes(typeof(AuthorizeAttribute), false).Cast<AuthorizeAttribute>()
            .Select(a => a.Policy);

        policies.Should().ContainSingle().Which.Should().Be(
            Meshmakers.Octo.Backend.AssetRepositoryServices.AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy);
    }
}
