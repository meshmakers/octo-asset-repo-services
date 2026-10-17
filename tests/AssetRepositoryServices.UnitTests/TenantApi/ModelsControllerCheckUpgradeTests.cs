using System.Reflection;
using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.CkModelMigrations;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AssetRepositoryServices.UnitTests.TenantApi;

/// <summary>
/// AB#6868: CheckUpgrade reports the tenant upgrade guard result (<c>isBlocked</c> / <c>blockers</c>) next to the
/// migration verdict, stays a read-only endpoint, starts no job and writes nothing. The members are additive:
/// the previous shape is unchanged and old clients simply ignore them.
/// </summary>
public class ModelsControllerCheckUpgradeTests
{
    private readonly ICatalogService _catalogService = A.Fake<ICatalogService>();
    private readonly ICkModelUpgradeService _upgradeService = A.Fake<ICkModelUpgradeService>();
    private readonly ICkModelLibraryStatusService _libraryStatusService = A.Fake<ICkModelLibraryStatusService>();
    private readonly ICommandClient<ImportCkCommandRequest> _importCk = A.Fake<ICommandClient<ImportCkCommandRequest>>();
    private readonly ICommandClient<ImportCkBatchCommandRequest> _importCkBatch =
        A.Fake<ICommandClient<ImportCkBatchCommandRequest>>();
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly ModelsController _controller;

    public ModelsControllerCheckUpgradeTests()
    {
        _controller = new ModelsController(
            A.Fake<IDistributedCacheService>(),
            A.Fake<ICommandClient<ExportRtByQueryCommandRequest>>(),
            A.Fake<ICommandClient<ExportRtByDeepGraphCommandRequest>>(),
            A.Fake<ICommandClient<ImportRtCommandRequest>>(),
            _importCk,
            _importCkBatch,
            _catalogService,
            A.Fake<ICkJsonSerializer>(),
            _systemContext,
            _upgradeService,
            A.Fake<ICkModelMigrationService>(),
            _libraryStatusService);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = "sbeg";
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        A.CallTo(() => _catalogService.IsExistingAsync(A<CkModelId>._)).Returns(true);
        A.CallTo(() => _upgradeService.CheckUpgradeNeededAsync("sbeg", "Basic", "2.5.0", A<CancellationToken>._))
            .Returns(new CkModelUpgradeInfo
            {
                CkModelName = "Basic", InstalledVersion = "2.4.0", TargetVersion = "2.5.0", UpgradeNeeded = true,
                MigrationPathAvailable = true, HasBreakingChanges = false
            });
    }

    private static ImportFromCatalogRequestDto Request() => new() { CatalogName = "Test", ModelId = "Basic-2.5.0" };

    [Fact]
    public async Task CheckUpgrade_ReturnsTheBlockers_NextToTheMigrationVerdict()
    {
        A.CallTo(() => _libraryStatusService.EvaluateUpgradeBlockersAsync("sbeg", A<CkModelId>._, A<CancellationToken>._))
            .Returns(new List<CkUpgradeBlockerDto>
            {
                new()
                {
                    Dependent = "EnergyCommunity.Registration-2.8.0", Update = "Basic-2.5.0", Kind = "ExactPin",
                    Subject = "Basic-2.4.0", Code = "CK_UPGRADE_EXACT_PIN", Message = "m"
                }
            });

        var result = await _controller.CheckUpgrade(Request(), TestContext.Current.CancellationToken);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should()
            .BeOfType<UpgradeCheckResponseDto>().Subject;
        dto.IsBlocked.Should().BeTrue();
        dto.Blockers.Should().ContainSingle().Which.Subject.Should().Be("Basic-2.4.0");
        // The model's own migration verdict is untouched: "no breaking changes" while the dependents are the problem.
        dto.HasBreakingChanges.Should().BeFalse();
        dto.UpgradeNeeded.Should().BeTrue();
    }

    [Fact]
    public async Task CheckUpgrade_NothingBlocked_IsNotBlocked_AndStartsNoJob()
    {
        A.CallTo(() => _libraryStatusService.EvaluateUpgradeBlockersAsync("sbeg", A<CkModelId>._, A<CancellationToken>._))
            .Returns(new List<CkUpgradeBlockerDto>());

        var result = await _controller.CheckUpgrade(Request(), TestContext.Current.CancellationToken);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should()
            .BeOfType<UpgradeCheckResponseDto>().Subject;
        dto.IsBlocked.Should().BeFalse();
        dto.Blockers.Should().BeEmpty();
        A.CallTo(_importCk).MustNotHaveHappened();
        A.CallTo(_importCkBatch).MustNotHaveHappened();
        A.CallTo(_systemContext).MustNotHaveHappened();
    }

    [Fact]
    public void CheckUpgrade_StaysUnderTheReadOnlyPolicy()
    {
        var method = typeof(ModelsController).GetMethod(nameof(ModelsController.CheckUpgrade))!;

        method.GetCustomAttribute<AuthorizeAttribute>()!.Policy.Should()
            .Be("TenantAssetApiReadOnlyPolicy");
        method.GetCustomAttribute<HttpPostAttribute>().Should().NotBeNull();
    }

    [Fact]
    public void Dtos_AreAdditive_PreviousShapeIsKeptAndOldPayloadsDeserializeToNeutralDefaults()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        // A previous client payload (without the new members) deserializes unchanged.
        var oldItem = JsonSerializer.Deserialize<CkModelLibraryStatusItemDto>(
            """{"name":"Basic","hasUpdate":true,"needsAction":true,"isCompatible":true}""", web)!;
        oldItem.IsBlocked.Should().BeFalse();
        oldItem.Blockers.Should().BeEmpty();
        oldItem.IsRangeRetaining.Should().BeFalse();
        oldItem.NeedsAction.Should().BeTrue();

        // The new response still contains every previous member under its previous name.
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new CkModelLibraryStatusItemDto { Name = "Basic" }, web));
        json.RootElement.EnumerateObject().Select(p => p.Name).Should().Contain([
            "name", "installedVersion", "modelState", "resolveFailedReason", "dependencies", "catalogVersion",
            "hasUpdate", "needsAction", "catalogName", "fullModelId", "isServiceManaged", "isCompatible",
            "incompatibilityReason", "unresolvedDependencies", "hasCatalogInconsistency",
            "isBlocked", "blockers", "isRangeRetaining"
        ]);
        using var check = JsonDocument.Parse(JsonSerializer.Serialize(new UpgradeCheckResponseDto(), web));
        check.RootElement.EnumerateObject().Select(p => p.Name).Should().Contain([
            "modelName", "installedVersion", "targetVersion", "upgradeNeeded", "migrationPathAvailable",
            "hasBreakingChanges", "errorMessage", "isBlocked", "blockers"
        ]);
    }

    [Fact]
    public void PreviousFixAllFilter_SkipsABlockedRow_BecauseNeedsActionIsFalse()
    {
        // octo-cli FixAll acts on rows with NeedsAction; a blocked row keeps HasUpdate but not NeedsAction.
        var rows = new[]
        {
            new CkModelLibraryStatusItemDto { Name = "Blocked", HasUpdate = true, NeedsAction = false, IsBlocked = true },
            new CkModelLibraryStatusItemDto { Name = "Free", HasUpdate = true, NeedsAction = true }
        };

        rows.Where(r => r.NeedsAction).Select(r => r.Name).Should().Equal("Free");
    }
}
