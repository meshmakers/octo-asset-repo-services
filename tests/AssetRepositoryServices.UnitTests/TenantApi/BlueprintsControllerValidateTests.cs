using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.BlueprintValidation;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AssetRepositoryServices.UnitTests.TenantApi;

/// <summary>
///     <c>POST blueprints/validate</c> hands the upload to the draft validation and answers 200 with its result, valid
///     or not; it needs only the read-only scope, has the size limit of <c>models/ValidateCk</c>, answers 400 without a
///     file and never touches the blueprint service.
/// </summary>
public class BlueprintsControllerValidateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IBlueprintService _blueprintService = A.Fake<IBlueprintService>();
    private readonly IBlueprintDraftValidationService _draftValidation = A.Fake<IBlueprintDraftValidationService>();

    private BlueprintsController CreateController(IBlueprintDraftValidationService? draftValidation)
    {
        var controller = new BlueprintsController(
            A.Fake<ITenantBlueprintHistory>(),
            _blueprintService,
            A.Fake<ITenantBlueprintInstallations>(),
            A.Fake<IAuthorizationService>(),
            NullLogger<BlueprintsController>.Instance,
            draftValidation);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = "draftie";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static IFormFile File(string name, byte[] bytes) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name)
        {
            Headers = new HeaderDictionary(), ContentType = "application/octet-stream"
        };

    [Fact]
    public async Task Validate_AnInvalidDraft_Is200WithTheFindings_AndTheBlueprintServiceIsNotUsed()
    {
        var invalid = new BlueprintDraftValidationResponseDto
        {
            IsValid = false,
            TotalFindings = 1,
            Findings = [new BlueprintDraftFindingDto { Code = 153, Level = "Error", Message = "m" }]
        };
        A.CallTo(() => _draftValidation.ValidateAsync(A<Stream>._, "draft.zip", A<CancellationToken>._))
            .Returns(invalid);

        var result = await CreateController(_draftValidation).Validate(File("draft.zip", [1, 2, 3]), Ct);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(invalid);
        A.CallTo(_blueprintService).MustNotHaveHappened();
    }

    [Fact]
    public async Task Validate_WithoutAFile_Is400_AndTheValidationIsNotCalled()
    {
        var controller = CreateController(_draftValidation);

        (await controller.Validate(null, Ct)).Should().BeOfType<BadRequestObjectResult>();
        (await controller.Validate(File("draft.zip", []), Ct)).Should().BeOfType<BadRequestObjectResult>();
        A.CallTo(_draftValidation).MustNotHaveHappened();
    }

    [Fact]
    public async Task Validate_WithoutTheValidationService_Is500_AndNothingElseRuns()
    {
        var result = await CreateController(null).Validate(File("draft.zip", [1, 2, 3]), Ct);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        A.CallTo(_blueprintService).MustNotHaveHappened();
    }

    [Fact]
    public void Validate_NeedsOnlyTheReadOnlyScope()
    {
        var method = typeof(BlueprintsController).GetMethod(nameof(BlueprintsController.Validate))!;
        var policies = method.GetCustomAttributes(typeof(AuthorizeAttribute), false).Cast<AuthorizeAttribute>()
            .Select(a => a.Policy);

        policies.Should().ContainSingle().Which.Should().Be(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy);
    }

    [Fact]
    public void Validate_HasTheSizeLimitOfValidateCk()
    {
        var method = typeof(BlueprintsController).GetMethod(nameof(BlueprintsController.Validate))!;
        var limit = method.GetCustomAttributes(typeof(RequestSizeLimitAttribute), false)
            .Cast<RequestSizeLimitAttribute>().Should().ContainSingle().Subject;

        // The attribute keeps its limit private; it is applied as a request size limit filter of the same value.
        limit.Should().NotBeNull();
        typeof(RequestSizeLimitAttribute).GetField("_bytes",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(limit).Should().Be(ModelsController.MaxValidateCkBytes);
    }
}
