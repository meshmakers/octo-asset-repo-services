using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AssetRepositoryServices.UnitTests.TenantApi;

/// <summary>
/// AB#7991: what a blueprint install or update did with the archives of its seed reaches the REST result (install and
/// update) and is an additive output field of the GraphQL result.
/// </summary>
public class BlueprintArchiveActivationResultTests
{
    private const string TenantId = "meshtest";
    private const string Target = "EnergyIQ.Base-1.1.0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IBlueprintService _blueprintService = A.Fake<IBlueprintService>();
    private readonly BlueprintsController _controller;

    public BlueprintArchiveActivationResultTests()
    {
        _controller = new BlueprintsController(A.Fake<ITenantBlueprintHistory>(), _blueprintService,
            A.Fake<ITenantBlueprintInstallations>(), A.Fake<IAuthorizationService>(),
            NullLogger<BlueprintsController>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    private static List<BlueprintArchiveActivation> Activations() =>
    [
        new()
        {
            ArchiveRtId = "6a0e000000000000000a0007", WellKnownName = "GridConnectionArchive",
            CkTypeId = "System.StreamData/RawArchive", BlueprintId = Target,
            Outcome = BlueprintArchiveActivationOutcome.Activated
        },
        new()
        {
            ArchiveRtId = "6a4aa4cc22664f2b26c67e6b", WellKnownName = "GridConnectionRollup1h",
            CkTypeId = "System.StreamData/RollupArchive", BlueprintId = Target, IsRollup = true,
            Outcome = BlueprintArchiveActivationOutcome.Activated,
            Backfill = BlueprintRollupBackfillOutcome.Queued, BackfillJobRtId = "6a0e0000000000000000aaaa"
        },
        new()
        {
            ArchiveRtId = "6a0e000000000000000a0001", CkTypeId = "System.StreamData/RawArchive", BlueprintId = Target,
            Outcome = BlueprintArchiveActivationOutcome.SkippedOptOut, Reason = "Listed in archiveActivation.exclude"
        }
    ];

    private static void AssertMapped(List<BlueprintArchiveActivationDto> dtos)
    {
        dtos.Should().HaveCount(3);
        dtos[0].Outcome.Should().Be("Activated");
        dtos[0].Backfill.Should().Be("NotApplicable");
        dtos[1].Should().BeEquivalentTo(new BlueprintArchiveActivationDto
        {
            ArchiveRtId = "6a4aa4cc22664f2b26c67e6b", WellKnownName = "GridConnectionRollup1h",
            CkTypeId = "System.StreamData/RollupArchive", BlueprintId = Target, IsRollup = true, Outcome = "Activated",
            Backfill = "Queued", BackfillJobRtId = "6a0e0000000000000000aaaa"
        });
        dtos[2].Outcome.Should().Be("SkippedOptOut");
        dtos[2].Reason.Should().Contain("archiveActivation.exclude");
    }

    [Fact]
    public async Task Apply_ReturnsTheArchiveActivations()
    {
        A.CallTo(() => _blueprintService.ApplyBlueprintAsync(TenantId, A<BlueprintId>._, A<BlueprintApplyOptions>._,
                A<CancellationToken>._))
            .Returns(BlueprintApplicationResult.Success(TenantId, new BlueprintId(Target), [], [], 3,
                new OperationResult(), null, null, Activations()));

        var result = await _controller.Apply(new BlueprintApplyRequestDto { BlueprintId = Target }, Ct);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should()
            .BeOfType<BlueprintApplyResultDto>().Subject;
        AssertMapped(dto.ArchiveActivations);
    }

    [Fact]
    public async Task ApplyUpdate_ReturnsTheArchiveActivations()
    {
        var engineResult = new BlueprintUpdateResult { Success = true };
        engineResult.ArchiveActivations.AddRange(Activations());
        A.CallTo(() => _blueprintService.ApplyUpdateAsync(TenantId, A<BlueprintId>._, A<BlueprintUpdateMode>._,
                A<BlueprintUpdateOptions?>._, A<CancellationToken>._))
            .Returns(engineResult);

        var result = await _controller.ApplyUpdate(new BlueprintUpdateRequestDto { TargetVersion = Target }, Ct);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should()
            .BeOfType<BlueprintUpdateResultDto>().Subject;
        AssertMapped(dto.ArchiveActivations);
    }

    [Fact]
    public void Apply_WithoutArchives_ReturnsAnEmptyList()
    {
        BlueprintUpdatePreviewMapper.ToDtos(Array.Empty<BlueprintArchiveActivation>()).Should().BeEmpty();
        new BlueprintApplyResultDto().ArchiveActivations.Should().BeEmpty();
    }

    [Fact]
    public void GraphQlApplyResult_ExposesTheArchiveActivations()
    {
        new BlueprintApplyResultDtoType().Fields.Find("archiveActivations").Should().NotBeNull();

        var type = new BlueprintArchiveActivationDtoType();
        foreach (var name in new[]
                 {
                     "archiveRtId", "wellKnownName", "ckTypeId", "blueprintId", "isRollup", "outcome", "reason",
                     "backfill", "backfillJobRtId", "backfillReason"
                 })
        {
            type.Fields.Find(name).Should().NotBeNull(name);
        }
    }
}
