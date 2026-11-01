using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.UpgradeGuard;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.UnitTests.Services;

/// <summary>
///     AB#7998: FixAll upgraded Basic.Energy 1.10.0 to 2.0.0 although the installed EnergyIQ 2.14.0 requires
///     <c>Basic.Energy-[1.9,2.0)</c>; EnergyIQ became ResolveFailed. The engine guard treats a candidate of another major
///     as a parallel install and reports nothing (faked here as "no blocker"), so the status service checks the ranges and
///     exact pins of the installed dependents itself, offers the highest allowed version and names who holds the newest
///     one back.
/// </summary>
public class CkDependentRequirementGuardTests
{
    private const string TenantId = "energyiq";

    private readonly ITenantContext _tenantContext = A.Fake<ITenantContext>();
    private readonly ICatalogService _catalogService = A.Fake<ICatalogService>();
    private readonly List<CkModel> _installed = [];
    private readonly Dictionary<string, CkCompiledModelRoot> _catalog = new();

    private static CkModel Installed(string fullId, CkModelId[]? pins = null, CkModelDependency[]? ranges = null,
        ModelState state = ModelState.Available)
    {
        var id = new CkModelId(fullId);
        return new CkModel
        {
            Id = id, ModelId = id.Name, ModelState = state, Dependencies = pins ?? [], DependencyRanges = ranges
        };
    }

    private static CkModel EnergyIqRangeRetaining(string version = "2.14.0") =>
        Installed($"EnergyIQ-{version}", [new CkModelId("Basic.Energy-1.9.0"), new CkModelId("System-2.7.0")],
            [new CkModelDependency { Range = "Basic.Energy-[1.9,2.0)", Floor = "1.9.0" }]);

    private static CkModel EnergyIqExactPinned() =>
        Installed("EnergyIQ-2.13.0", [new CkModelId("Basic.Energy-1.10.0")]);

    private static CkCompiledModelRoot Compiled(string fullId, CkModelId[]? pins = null,
        List<CkModelDependencyDto>? ranges = null) =>
        new() { ModelId = new CkModelId(fullId), Dependencies = pins?.ToList() ?? [], DependencyRanges = ranges };

    private void Offer(params string[] fullIds)
    {
        foreach (var id in fullIds)
        {
            _catalog[id] = Compiled(id);
        }
    }

    private CkModelLibraryStatusService CreateSut()
    {
        var resultSet = A.Fake<IResultSet<CkModel>>();
        A.CallTo(() => resultSet.Items).ReturnsLazily(() => _installed.ToArray());
        var repository = A.Fake<ITenantRepository>();
        A.CallTo(() => repository.GetCkModelsAsync(A<IOctoSession>._, A<List<CkModelId>?>._, A<RtEntityQueryOptions>._,
            A<int?>._, A<int?>._)).Returns(resultSet);
        A.CallTo(() => _tenantContext.GetTenantRepository()).Returns(repository);
        // The engine guard does not judge a candidate of another major (parallel install): no blocker.
        A.CallTo(() => _tenantContext.EvaluateCkUpdateAsync(A<IReadOnlyCollection<CkCompiledModelRoot>>._,
            A<IReadOnlyCollection<CkModelId>?>._, A<CancellationToken>._)).Returns(CkUpgradeGuardResult.None);
        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.FindTenantContextAsync(TenantId)).Returns(_tenantContext);

        A.CallTo(() => _catalogService.ListAsync(A<int>._, A<int>._, A<object?>._, A<CancellationToken?>._))
            .ReturnsLazily(() => new ModelListResult
            {
                TotalCount = _catalog.Count, SkippedCount = 0, TakeCount = 500,
                ModelResultItems = _catalog.Values.Select(m => new CatalogResultItem
                    { ModelId = m.ModelId, CatalogName = "PublicGitHubCatalog", Description = m.ModelId.Name }).ToList()
            });
        A.CallTo(() => _catalogService.GetAsync(A<CkModelId>._, A<OperationResult>._, A<object?>._,
                A<CancellationToken?>._))
            .ReturnsLazily((CkModelId id, OperationResult _, object? _, CancellationToken? _) =>
                _catalog.TryGetValue(id.FullName, out var m)
                    ? Task.FromResult<CkCompiledModelRoot?>(m)
                    : throw ModelCatalogException.ModelNotFound(id, "PublicGitHubCatalog"));

        return new CkModelLibraryStatusService(_catalogService, systemContext);
    }

    // ---- pure check ------------------------------------------------------------------------------------------------

    [Fact]
    public void Evaluate_NextMajor_IsBlockedByRangeRetainingDependent()
    {
        var installed = new[] { Installed("Basic.Energy-1.10.0"), EnergyIqRangeRetaining() };

        var blockers = CkDependentRequirementGuard.Evaluate(installed, [Compiled("Basic.Energy-2.0.0")], false);

        var blocker = blockers.Should().ContainSingle().Subject;
        blocker.Dependent.Should().Be("EnergyIQ-2.14.0");
        blocker.Update.Should().Be("Basic.Energy-2.0.0");
        blocker.Kind.Should().Be("RangeNotSatisfied");
        blocker.Code.Should().Be("CK_UPGRADE_RANGE_NOT_SATISFIED");
        blocker.Subject.Should().Be("Basic.Energy-[1.9,2.0)");
        blocker.Message.Should().Be(
            "EnergyIQ 2.14.0 would break: range Basic.Energy-[1.9,2.0) (floor 1.9.0) does not admit Basic.Energy-2.0.0");
        blocker.DependentIsInUpdateSet.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_VersionInsideTheRange_IsNotBlocked()
    {
        var installed = new[] { Installed("Basic.Energy-1.10.0"), EnergyIqRangeRetaining() };

        CkDependentRequirementGuard.Evaluate(installed, [Compiled("Basic.Energy-1.11.0")], false).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_ExactPinnedDependent_BlocksEveryOtherVersion_AlsoAcrossMajors()
    {
        var installed = new[] { Installed("Basic.Energy-1.10.0"), EnergyIqExactPinned() };

        CkDependentRequirementGuard.Evaluate(installed, [Compiled("Basic.Energy-2.0.0")], false)
            .Should().ContainSingle().Which.Should().BeEquivalentTo(new
            {
                Dependent = "EnergyIQ-2.13.0", Kind = "ExactPin", Code = "CK_UPGRADE_EXACT_PIN",
                Subject = "Basic.Energy-1.10.0"
            });
        CkDependentRequirementGuard.Evaluate(installed, [Compiled("Basic.Energy-1.11.0")], false)
            .Should().ContainSingle().Which.Kind.Should().Be("ExactPin");
    }

    [Fact]
    public void Evaluate_DependentUpdatedInTheSameSet_IsJudgedByItsNewRange()
    {
        var installed = new[] { Installed("Basic.Energy-1.10.0"), EnergyIqRangeRetaining() };
        var energyIq3 = Compiled("EnergyIQ-3.0.0", [new CkModelId("Basic.Energy-2.0.0")],
            [new CkModelDependencyDto { Range = new CkModelIdVersionRange("Basic.Energy-[2.0,3.0)"), Floor = "2.0.0" }]);

        CkDependentRequirementGuard.Evaluate(installed, [Compiled("Basic.Energy-2.0.0"), energyIq3], false)
            .Should().BeEmpty("EnergyIQ is replaced by 3.0.0, which accepts Basic.Energy 2.x");
    }

    [Fact]
    public void Evaluate_ModelOfTheSetThatExcludesTheCandidate_IsABlockerInTheSet()
    {
        var installed = new[] { Installed("Basic.Energy-1.10.0"), Installed("EnergyIQ-2.14.0") };
        var energyIq215 = Compiled("EnergyIQ-2.15.0", [new CkModelId("Basic.Energy-1.9.0")],
            [new CkModelDependencyDto { Range = new CkModelIdVersionRange("Basic.Energy-[1.9,2.0)"), Floor = "1.9.0" }]);

        CkDependentRequirementGuard.Evaluate(installed, [Compiled("Basic.Energy-2.0.0"), energyIq215], false)
            .Should().ContainSingle().Which.Should().BeEquivalentTo(new
                { Dependent = "EnergyIQ-2.15.0", DependentIsInUpdateSet = true });
    }

    [Fact]
    public void Evaluate_TransitivePinOfARangeRetainingDependent_IsNotJudged()
    {
        // EnergyIQ lists System-2.7.0 in its flat closure but has no range for System (AB#7940): not its requirement.
        var installed = new[] { Installed("System-2.7.0"), EnergyIqRangeRetaining() };

        CkDependentRequirementGuard.Evaluate(installed, [Compiled("System-3.0.0")], true).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_ServiceManagedCandidate_IsOnlyJudgedForExplicitCallers()
    {
        var installed = new[] { Installed("System-2.7.0"), Installed("Legacy-1.0.0", [new CkModelId("System-2.7.0")]) };

        CkDependentRequirementGuard.Evaluate(installed, [Compiled("System-3.0.0")], false).Should().BeEmpty();
        CkDependentRequirementGuard.Evaluate(installed, [Compiled("System-3.0.0")], true)
            .Should().ContainSingle().Which.Dependent.Should().Be("Legacy-1.0.0");
    }

    [Fact]
    public void Evaluate_IgnoresResolveFailedDependents_AndCandidatesThatAreNotInstalled()
    {
        var installed = new[]
        {
            Installed("Basic.Energy-1.10.0"),
            Installed("EnergyIQ-2.14.0", [new CkModelId("Basic.Energy-1.10.0")], state: ModelState.ResolveFailed),
            Installed("Other-1.0.0", [new CkModelId("New-1.0.0")])
        };

        CkDependentRequirementGuard.Evaluate(installed, [Compiled("Basic.Energy-2.0.0"), Compiled("New-2.0.0")], false)
            .Should().BeEmpty();
    }

    // ---- library status rows ---------------------------------------------------------------------------------------

    [Fact]
    public async Task LibraryStatus_RangeRetainingDependent_OffersTheHighestAllowedVersion_AndNamesTheHeldBackOne()
    {
        _installed.AddRange([Installed("Basic.Energy-1.10.0"), EnergyIqRangeRetaining()]);
        Offer("Basic.Energy-1.10.0", "Basic.Energy-1.11.0", "Basic.Energy-2.0.0");

        var status = await CreateSut().GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        var row = status.Items.Single(i => i.Name == "Basic.Energy");
        row.CatalogVersion.Should().Be("1.11.0");
        row.FullModelId.Should().Be("Basic.Energy-1.11.0");
        row.CatalogName.Should().Be("PublicGitHubCatalog");
        row.HasUpdate.Should().BeTrue();
        row.NeedsAction.Should().BeTrue("FixAll imports the highest allowed version");
        row.IsBlocked.Should().BeFalse();
        row.Blockers.Should().BeEmpty();
        row.HeldBackVersion.Should().Be("2.0.0");
        row.HeldBackBy.Should().ContainSingle().Which.Should().BeEquivalentTo(new
            { Dependent = "EnergyIQ-2.14.0", Subject = "Basic.Energy-[1.9,2.0)", Update = "Basic.Energy-2.0.0" });
        row.HeldBackReason.Should().Be("2.0.0 available, held back by EnergyIQ 2.14.0 (Basic.Energy-[1.9,2.0))");
    }

    [Fact]
    public async Task LibraryStatus_NoAllowedNewerVersion_IsBlocked_AndNotOfferedAsUpdate()
    {
        _installed.AddRange([Installed("Basic.Energy-1.10.0"), EnergyIqRangeRetaining()]);
        Offer("Basic.Energy-1.10.0", "Basic.Energy-2.0.0");

        var status = await CreateSut().GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        var row = status.Items.Single(i => i.Name == "Basic.Energy");
        row.NeedsAction.Should().BeFalse("the only newer version would break EnergyIQ");
        row.IsBlocked.Should().BeTrue();
        row.CatalogVersion.Should().Be("2.0.0");
        row.Blockers.Should().ContainSingle().Which.Dependent.Should().Be("EnergyIQ-2.14.0");
        row.HeldBackVersion.Should().Be("2.0.0");
        row.HeldBackReason.Should().Contain("held back by EnergyIQ 2.14.0");
        status.ModelsNeedingActionCount.Should().Be(0);
    }

    [Fact]
    public async Task LibraryStatus_ExactPinnedDependent_HoldsEveryNewerVersionBack()
    {
        _installed.AddRange([Installed("Basic.Energy-1.10.0"), EnergyIqExactPinned()]);
        Offer("Basic.Energy-1.10.0", "Basic.Energy-1.11.0", "Basic.Energy-2.0.0");

        var status = await CreateSut().GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        var row = status.Items.Single(i => i.Name == "Basic.Energy");
        row.NeedsAction.Should().BeFalse();
        row.IsBlocked.Should().BeTrue();
        row.HeldBackBy.Should().ContainSingle().Which.Subject.Should().Be("Basic.Energy-1.10.0");
    }

    [Fact]
    public async Task LibraryStatus_NothingHeldBack_KeepsTheNewestVersion()
    {
        _installed.AddRange([Installed("Basic.Energy-1.10.0"), EnergyIqRangeRetaining()]);
        Offer("Basic.Energy-1.10.0", "Basic.Energy-1.11.0");

        var status = await CreateSut().GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        var row = status.Items.Single(i => i.Name == "Basic.Energy");
        row.CatalogVersion.Should().Be("1.11.0");
        row.NeedsAction.Should().BeTrue();
        row.HeldBackVersion.Should().BeNull();
        row.HeldBackBy.Should().BeEmpty();
        row.HeldBackReason.Should().BeNull();
    }

    [Fact]
    public async Task EvaluateUpdateSetBlockers_NextMajorPastADependentsRange_NamesTheDependent()
    {
        _installed.AddRange([Installed("Basic.Energy-1.10.0"), EnergyIqRangeRetaining()]);
        Offer("Basic.Energy-2.0.0");

        var blockers = await CreateSut().EvaluateUpdateSetBlockersAsync(TenantId, [new CkModelId("Basic.Energy-2.0.0")],
            TestContext.Current.CancellationToken);

        blockers.Should().ContainSingle().Which.Dependent.Should().Be("EnergyIQ-2.14.0");
    }

    [Fact]
    public async Task EvaluateUpdateSetBlockers_PairTheEngineReported_IsNotReportedTwice()
    {
        _installed.AddRange([Installed("Basic.Energy-1.10.0"), EnergyIqExactPinned()]);
        Offer("Basic.Energy-1.11.0");
        var sut = CreateSut();
        A.CallTo(() => _tenantContext.EvaluateCkUpdateAsync(A<IReadOnlyCollection<CkCompiledModelRoot>>._,
                A<IReadOnlyCollection<CkModelId>?>._, A<CancellationToken>._))
            .Returns(new CkUpgradeGuardResult([
                new CkUpgradeBlocker(new CkModelId("EnergyIQ-2.13.0"), new CkModelId("Basic.Energy-1.11.0"),
                    CkUpgradeBlockerKind.ExactPin, "Basic.Energy-1.10.0", "CK_UPGRADE_EXACT_PIN",
                    "exact pin Basic.Energy-1.10.0: Basic.Energy-1.11.0 replaces it", false, null)
            ], []));

        var blockers = await sut.EvaluateUpdateSetBlockersAsync(TenantId, [new CkModelId("Basic.Energy-1.11.0")],
            TestContext.Current.CancellationToken);

        blockers.Should().ContainSingle();
    }
}
