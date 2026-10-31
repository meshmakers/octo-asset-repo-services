using System.Text.Json;
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
/// AB#7408: the compatibility check behind LibraryStatus and ImportFromCatalog honours the
/// <c>dependencyRanges</c> of range-retaining (CK v2) catalog models; exact-pinned models keep the strict check.
/// </summary>
public class CkModelLibraryStatusDependencyRangeTests
{
    private const string TenantId = "test-tenant";

    private readonly ICatalogService _catalogService = A.Fake<ICatalogService>();
    private readonly CkModelLibraryStatusService _sut;
    private readonly Dictionary<string, CkCompiledModelRoot> _models = new();
    private readonly ITenantRepository _repository = A.Fake<ITenantRepository>();
    private readonly ITenantContext _tenantContext = A.Fake<ITenantContext>();

    public CkModelLibraryStatusDependencyRangeTests()
    {
        var installed = A.Fake<IResultSet<CkModel>>();
        A.CallTo(() => installed.Items).Returns(new[]
        {
            new CkModel
            {
                Id = new CkModelId("System-2.7.0"), ModelId = "System",
                ModelState = ModelState.Available
            }
        });
        var repository = _repository;
        A.CallTo(() => repository.GetCkModelsAsync(A<IOctoSession>._, A<List<CkModelId>?>._, A<RtEntityQueryOptions>._,
                A<int?>._, A<int?>._)).Returns(installed);
        var tenantContext = _tenantContext;
        A.CallTo(() => tenantContext.EvaluateCkUpdateAsync(A<IReadOnlyCollection<CkCompiledModelRoot>>._,
                A<IReadOnlyCollection<CkModelId>?>._, A<CancellationToken>._))
            .Returns(CkUpgradeGuardResult.None);
        A.CallTo(() => tenantContext.GetTenantRepository()).Returns(repository);
        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.FindTenantContextAsync(TenantId)).Returns(tenantContext);

        A.CallTo(() => _catalogService.GetAsync(A<CkModelId>._, A<OperationResult>._, A<object?>._,
                A<CancellationToken?>._))
            .ReturnsLazily((CkModelId id, OperationResult _, object? _, CancellationToken? _) =>
                _models.TryGetValue(id.FullName, out var m)
                    ? Task.FromResult<CkCompiledModelRoot?>(m)
                    : throw ModelCatalogException.ModelNotFound(id, "Test"));

        _sut = new CkModelLibraryStatusService(_catalogService, systemContext);
    }

    private static CkModelDependencyDto Range(string range, string floor) =>
        new() { Range = new CkModelIdVersionRange(range.Split('-', 2)[0], range.Split('-', 2)[1]), Floor = floor };

    private static CkCompiledModelRoot Exact(string name, string version, string systemPin) =>
        new() { ModelId = new CkModelId(name, version), Dependencies = [new CkModelId(systemPin)] };

    private static CkCompiledModelRoot Ranged(string name, string version, string systemFloor, string range) =>
        new()
        {
            ModelId = new CkModelId(name, version),
            Dependencies = [new CkModelId($"System-{systemFloor}")],
            DependencyRanges = [Range(range, systemFloor)]
        };

    private Task<(bool ok, string? reason)> Check(CkCompiledModelRoot model, string installedSystem)
    {
        _models[model.ModelId.FullName] = model;
        return CheckAsync(model.ModelId, installedSystem);
    }

    private Task<(bool ok, string? reason)> CheckAsync(CkModelId id, string installedSystem) =>
        _sut.CheckSystemCompatibilityAsync(id,
            new Dictionary<string, CkVersion> { ["System"] = new(installedSystem) }, new HashSet<string>(), [],
            CancellationToken.None);

    [Fact]
    public async Task RangeContainsInstalledSystem_IsCompatible()
    {
        var (ok, reason) = await Check(Ranged("Basic", "2.5.0", "2.5.0", "System-[2.5,3.0)"), "2.7.0");

        ok.Should().BeTrue(reason);
        reason.Should().BeNull();
    }

    [Fact]
    public async Task RangeDoesNotContainInstalledSystem_IsIncompatibleWithReason()
    {
        var (ok, reason) = await Check(Ranged("Basic", "2.5.0", "2.5.0", "System-[2.5,3.0)"), "3.1.0");

        ok.Should().BeFalse();
        reason.Should().Contain("System-[2.5,3.0)").And.Contain("System-3.1.0 is installed");
    }

    [Fact]
    public async Task InstalledSystemBelowFloor_IsIncompatible()
    {
        var (ok, _) = await Check(Ranged("Basic", "2.5.0", "2.5.0", "System-[2.5,3.0)"), "2.4.0");

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task ExactPin_StillRequiresExactInstalledVersion()
    {
        var (ok, reason) = await Check(Exact("Basic", "2.4.0", "System-2.5.0"), "2.7.0");

        ok.Should().BeFalse();
        reason.Should().Be("Requires System-2.5.0, but System-2.7.0 is installed");
    }

    [Fact]
    public async Task ExactPin_MatchingInstalledVersion_IsCompatible()
    {
        var (ok, _) = await Check(Exact("Basic", "2.4.0", "System-2.5.0"), "2.5.0");

        ok.Should().BeTrue();
    }

    [Fact]
    public async Task TransitiveDependency_IsJudgedByItsOwnRangeOrPin()
    {
        _models["Basic-2.5.0"] = Ranged("Basic", "2.5.0", "2.5.0", "System-[2.5,3.0)");
        var top = Exact("Top", "1.0.0", "System-2.7.0");
        top.Dependencies!.Add(new CkModelId("Basic-2.5.0"));
        _models[top.ModelId.FullName] = top;
        (await CheckAsync(top.ModelId, "2.7.0")).ok.Should().BeTrue();

        _models["Basic-2.5.0"] = Exact("Basic", "2.5.0", "System-2.5.0");
        (await CheckAsync(top.ModelId, "2.7.0")).ok.Should().BeFalse();
    }

    /// <summary>
    /// AB#7940: a range-retaining model whose dependencies list carries the transitive System pin (EnergyIQ via Basic)
    /// but whose dependencyRanges name only the direct dependencies.
    /// </summary>
    private static CkCompiledModelRoot RangedWithTransitiveSystem(string name, string version, string intermediate,
        string intermediateRange, string transitiveSystemPin) =>
        new()
        {
            ModelId = new CkModelId(name, version),
            Dependencies = [new CkModelId(intermediate), new CkModelId(transitiveSystemPin)],
            DependencyRanges = [Range(intermediateRange, intermediate.Split('-', 2)[1])]
        };

    [Fact]
    public async Task Transitive_System_ViaRangeRetainingIntermediate_IsCompatibleWithNewerSystem()
    {
        _models["Basic-2.5.0"] = Ranged("Basic", "2.5.0", "2.5.0", "System-[2.5,3.0)");
        var top = RangedWithTransitiveSystem("EnergyIQ", "2.14.0", "Basic-2.5.0", "Basic-[2.5,3.0)", "System-2.7.0");

        var (ok, reason) = await Check(top, "2.8.0");

        ok.Should().BeTrue(reason);
    }

    [Fact]
    public async Task Transitive_System_ViaRangeRetainingIntermediate_RangeExcludesInstalled_IsIncompatibleWithReason()
    {
        _models["Basic-2.5.0"] = Ranged("Basic", "2.5.0", "2.5.0", "System-[2.5,3.0)");
        var top = RangedWithTransitiveSystem("EnergyIQ", "2.14.0", "Basic-2.5.0", "Basic-[2.5,3.0)", "System-2.7.0");

        var (ok, reason) = await Check(top, "3.1.0");

        ok.Should().BeFalse();
        reason.Should().Contain("System-[2.5,3.0)").And.Contain("System-3.1.0 is installed");
    }

    [Fact]
    public async Task Transitive_System_ViaExactPinnedIntermediate_StaysIncompatible()
    {
        _models["Basic-2.4.0"] = Exact("Basic", "2.4.0", "System-2.7.0");
        var top = RangedWithTransitiveSystem("EnergyIQ", "2.14.0", "Basic-2.4.0", "Basic-[2.4,3.0)", "System-2.7.0");

        var (ok, reason) = await Check(top, "2.8.0");

        ok.Should().BeFalse();
        reason.Should().Be("Requires System-2.7.0, but System-2.8.0 is installed");
    }

    [Fact]
    public async Task Transitive_System_OfRangeRetainingModel_IsNotMarkedVisited_SoIntermediateStillChecksItsRange()
    {
        // Same System pin top and intermediate: skipping it at the top must not make the intermediate skip it too.
        _models["Basic-2.5.0"] = Ranged("Basic", "2.5.0", "2.7.0", "System-[2.7,2.8)");
        var top = RangedWithTransitiveSystem("EnergyIQ", "2.14.0", "Basic-2.5.0", "Basic-[2.5,3.0)", "System-2.7.0");

        var (ok, reason) = await Check(top, "2.8.0");

        ok.Should().BeFalse();
        reason.Should().Contain("System-[2.7,2.8)");
    }

    [Fact]
    public async Task DirectSystemRange_OnRangeRetainingModel_IsUnchanged()
    {
        var model = Ranged("System.Communication", "4.13.0", "2.7.0", "System-[2.7,3.0)");

        (await Check(model, "2.8.0")).ok.Should().BeTrue();
        (await Check(model, "3.0.0")).ok.Should().BeFalse();
    }

    [Fact]
    public void CatalogManifestJson_KeepsDependencyRanges()
    {
        const string json = """
            {
              "modelId": "Basic-2.5.0",
              "dependencies": ["System-2.5.0"],
              "dependencyRanges": [ { "range": "System-[2.5,3.0)", "floor": "2.5.0" } ]
            }
            """;

        var model = JsonSerializer.Deserialize<CkCompiledModelRoot>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        model!.IsRangeRetaining.Should().BeTrue();
        model.DependencyRanges!.Single().GetEffectiveRange().FullName.Should().Be("System-[2.5,3.0)");
    }

    [Fact]
    public async Task LibraryStatus_CatalogOnlyRows_ReportRangeRetentionAndCompatibility()
    {
        _models["Basic-2.5.0"] = Ranged("Basic", "2.5.0", "2.5.0", "System-[2.5,3.0)");
        _models["Old-1.0.0"] = Exact("Old", "1.0.0", "System-2.5.0");
        A.CallTo(() => _catalogService.ListAsync(A<int>._, A<int>._, A<object?>._, A<CancellationToken?>._))
            .Returns(new ModelListResult
            {
                TotalCount = 2, SkippedCount = 0, TakeCount = 500,
                ModelResultItems =
                [
                    new CatalogResultItem { ModelId = new CkModelId("Basic-2.5.0"), CatalogName = "T", Description = "" },
                    new CatalogResultItem { ModelId = new CkModelId("Old-1.0.0"), CatalogName = "T", Description = "" }
                ]
            });

        var status = await _sut.GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        var basic = status.Items.Single(i => i.Name == "Basic");
        basic.IsCompatible.Should().BeTrue();
        basic.IsRangeRetaining.Should().BeTrue();
        var old = status.Items.Single(i => i.Name == "Old");
        old.IsCompatible.Should().BeFalse();
        old.IsRangeRetaining.Should().BeFalse();
        old.IncompatibilityReason.Should().Be("Requires System-2.5.0, but System-2.7.0 is installed");
    }

    /// <summary>
    /// AB#7567: the installed-row scenario of the 2026-10-26 System 2.7.0 bump. Installed Basic-2.4.0 is
    /// ResolveFailed (pin System-[2.5.0]); the catalog holds the range-retaining Basic-2.5.0 (range System-[2.5,3.0)).
    /// FixAll imports exactly the rows with NeedsAction and IsCompatible, so the row must carry both.
    /// </summary>
    [Fact]
    public async Task LibraryStatus_ResolveFailedClassicModel_WithRangeRetainingSuccessor_NeedsActionAndIsCompatible()
    {
        SeedInstalled(
            new CkModel { Id = new CkModelId("System-2.7.0"), ModelId = "System", ModelState = ModelState.Available },
            new CkModel
            {
                Id = new CkModelId("Basic-2.4.0"), ModelId = "Basic", ModelState = ModelState.ResolveFailed,
                ResolveFailedReason = "pin System-[2.5.0]", Dependencies = [new CkModelId("System-2.5.0")]
            });
        _models["Basic-2.5.0"] = Ranged("Basic", "2.5.0", "2.5.0", "System-[2.5,3.0)");
        SetCatalog("Basic-2.5.0");

        var status = await _sut.GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        var basic = status.Items.Single(i => i.Name == "Basic");
        basic.IsCompatible.Should().BeTrue(basic.IncompatibilityReason);
        basic.NeedsAction.Should().BeTrue();
        basic.IsBlocked.Should().BeFalse();
        basic.FullModelId.Should().Be("Basic-2.5.0");
        status.ModelsNeedingActionCount.Should().Be(1);
    }

    /// <summary>AB#7567: a ResolveFailed model whose only catalog successor is classic-pinned to another System stays listed, not actionable.</summary>
    [Fact]
    public async Task LibraryStatus_ResolveFailedModel_WithClassicSuccessorPinnedToOtherSystem_StaysIncompatible()
    {
        SeedInstalled(
            new CkModel { Id = new CkModelId("System-2.7.0"), ModelId = "System", ModelState = ModelState.Available },
            new CkModel
            {
                Id = new CkModelId("Basic-2.4.0"), ModelId = "Basic", ModelState = ModelState.ResolveFailed,
                Dependencies = [new CkModelId("System-2.5.0")]
            });
        _models["Basic-2.4.1"] = Exact("Basic", "2.4.1", "System-2.5.0");
        SetCatalog("Basic-2.4.1");

        var status = await _sut.GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        var basic = status.Items.Single(i => i.Name == "Basic");
        basic.IsCompatible.Should().BeFalse();
        basic.NeedsAction.Should().BeFalse();
        basic.IncompatibilityReason.Should().Be("Requires System-2.5.0, but System-2.7.0 is installed");
        basic.FullModelId.Should().Be("Basic-2.4.1");
    }

    /// <summary>AB#7567: the tenant upgrade guard still decides after the range check: a compatible but blocked row is not actionable.</summary>
    [Fact]
    public async Task LibraryStatus_RangeRetainingSuccessor_StillBlockedByUpgradeGuard_HasNoNeedsAction()
    {
        SeedInstalled(
            new CkModel { Id = new CkModelId("System-2.7.0"), ModelId = "System", ModelState = ModelState.Available },
            new CkModel
            {
                Id = new CkModelId("Basic-2.4.0"), ModelId = "Basic", ModelState = ModelState.Available,
                Dependencies = [new CkModelId("System-2.7.0")]
            });
        _models["Basic-2.5.0"] = Ranged("Basic", "2.5.0", "2.5.0", "System-[2.5,3.0)");
        SetCatalog("Basic-2.5.0");
        var blocker = new CkUpgradeBlocker(new CkModelId("Dep-1.0.0"), new CkModelId("Basic-2.5.0"),
            CkUpgradeBlockerKind.ExactPin, "Basic-2.4.0", "CK_UPGRADE_EXACT_PIN", "exact pin Basic-2.4.0", false, null);
        A.CallTo(() => _tenantContext.EvaluateCkUpdateAsync(A<IReadOnlyCollection<CkCompiledModelRoot>>._,
                A<IReadOnlyCollection<CkModelId>?>._, A<CancellationToken>._))
            .Returns(new CkUpgradeGuardResult([blocker], []));

        var status = await _sut.GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        var basic = status.Items.Single(i => i.Name == "Basic");
        basic.IsCompatible.Should().BeTrue();
        basic.IsBlocked.Should().BeTrue();
        basic.NeedsAction.Should().BeFalse();
    }

    private void SeedInstalled(params CkModel[] models)
    {
        var resultSet = A.Fake<IResultSet<CkModel>>();
        A.CallTo(() => resultSet.Items).Returns(models);
        A.CallTo(() => _repository.GetCkModelsAsync(A<IOctoSession>._, A<List<CkModelId>?>._, A<RtEntityQueryOptions>._,
            A<int?>._, A<int?>._)).Returns(resultSet);
    }

    private void SetCatalog(params string[] ids)
    {
        A.CallTo(() => _catalogService.ListAsync(A<int>._, A<int>._, A<object?>._, A<CancellationToken?>._))
            .Returns(new ModelListResult
            {
                TotalCount = ids.Length, SkippedCount = 0, TakeCount = 500,
                ModelResultItems = ids.Select(i => new CatalogResultItem
                    { ModelId = new CkModelId(i), CatalogName = "T", Description = "" }).ToList()
            });
    }
}
