using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Xunit;

namespace AssetRepositoryServices.UnitTests.Services;

/// <summary>
/// AB#6871: the asset repository's guard for blueprints judges the CK models of a blueprint with the same evaluation
/// the library status and the imports use, leaves service-managed models alone and maps the blockers one to one.
/// </summary>
public class BlueprintCkUpgradeGuardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ICkModelLibraryStatusService _status = A.Fake<ICkModelLibraryStatusService>();

    [Fact]
    public async Task Evaluate_JudgesTheSetAsOne_AndMapsTheBlockers()
    {
        A.CallTo(() => _status.EvaluateUpdateSetBlockersAsync("sbeg", A<IReadOnlyList<CkModelId>>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<CkUpgradeBlockerDto>>(
            [
                new CkUpgradeBlockerDto
                {
                    Dependent = "EnergyCommunity.Registration-2.8.0", Update = "Basic-2.5.0", Kind = "ExactPin",
                    Subject = "Basic-2.4.0", Code = "CK_UPGRADE_EXACT_PIN", Message = "m",
                    DependentIsInUpdateSet = false, SuggestedDependentUpdate = "EnergyCommunity.Registration-2.10.0",
                    ResolveHint = "update first"
                }
            ]));

        var blockers = await new BlueprintCkUpgradeGuard(_status).EvaluateAsync("sbeg",
            [new CkModelId("Basic", "2.5.0"), new CkModelId("Basic.Energy", "1.10.0")], Ct);

        var blocker = blockers.Should().ContainSingle().Subject;
        blocker.Dependent.Should().Be("EnergyCommunity.Registration-2.8.0");
        blocker.Update.Should().Be("Basic-2.5.0");
        blocker.Kind.Should().Be("ExactPin");
        blocker.Subject.Should().Be("Basic-2.4.0");
        blocker.Code.Should().Be("CK_UPGRADE_EXACT_PIN");
        blocker.SuggestedDependentUpdate.Should().Be("EnergyCommunity.Registration-2.10.0");
        blocker.ResolveHint.Should().Be("update first");
        A.CallTo(() => _status.EvaluateUpdateSetBlockersAsync("sbeg",
                A<IReadOnlyList<CkModelId>>.That.Matches(s => s.Count == 2), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Evaluate_ServiceManagedModelsAreNotJudged()
    {
        await new BlueprintCkUpgradeGuard(_status).EvaluateAsync("sbeg",
            [new CkModelId("System", "2.6.0"), new CkModelId("System.UI", "2.3.0"), new CkModelId("Basic", "2.5.0")], Ct);

        A.CallTo(() => _status.EvaluateUpdateSetBlockersAsync("sbeg",
                A<IReadOnlyList<CkModelId>>.That.Matches(s => s.Count == 1 && s[0].Name == "Basic"),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Evaluate_OnlyServiceManagedModels_AsksNobody()
    {
        var blockers = await new BlueprintCkUpgradeGuard(_status).EvaluateAsync("sbeg",
            [new CkModelId("System", "2.6.0")], Ct);

        blockers.Should().BeEmpty();
        A.CallTo(_status).MustNotHaveHappened();
    }
}
