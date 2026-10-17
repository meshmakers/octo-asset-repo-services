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
/// AB#6868: LibraryStatus consults the tenant upgrade guard (AB#6867) for the offered catalog version of each row
/// (read-only): a blocked row keeps HasUpdate and loses NeedsAction. The guard itself is faked here - its own
/// behaviour is covered in octo-construction-kit-engine-mongodb; these tests pin how the status service uses it.
/// The scenario is the staging-1 tenant sbeg: Registration 2.8.0 is exact-pinned to Basic-2.4.0 / Basic.Energy-1.9.0.
/// </summary>
public class CkModelLibraryStatusUpgradeGuardTests
{
    private const string TenantId = "sbeg";
    private const string Registration = "EnergyCommunity.Registration";

    private readonly ITenantContext _tenantContext = A.Fake<ITenantContext>();
    private readonly ICatalogService _catalogService = A.Fake<ICatalogService>();
    private readonly List<CkModel> _installed = [];
    private readonly List<CkModelId> _offered = [];
    private readonly List<IReadOnlyCollection<CkModelId>> _updateSetsEvaluated = [];

    /// <summary>Update sets (by full id) for which the faked guard reports no blocker.</summary>
    private Func<IReadOnlyCollection<CkCompiledModelRoot>, bool> _guardIsClear = _ => false;

    private static CkModelId Id(string name, string version) => new(name, version);

    private static CkModel Installed(string name, string version, ModelState state = ModelState.Available,
        bool rangeRetaining = false) =>
        new()
        {
            Id = Id(name, version), ModelId = name, ModelState = state,
            DependencyRanges = rangeRetaining
                ? [new CkModelDependency { Range = "Basic-[2.4,3.0)" }]
                : null
        };

    private void SeedSbeg()
    {
        _installed.AddRange([
            Installed("System", "2.0.0"),
            Installed("Basic", "2.4.0"),
            Installed("Basic.Energy", "1.9.0"),
            Installed("EnergyCommunity", "4.9.0"),
            Installed(Registration, "2.8.0")
        ]);
        _offered.AddRange([Id("Basic", "2.5.0"), Id("Basic.Energy", "1.10.0"), Id("EnergyCommunity", "4.10.0")]);
    }

    private CkModelLibraryStatusService CreateSut()
    {
        var resultSet = A.Fake<IResultSet<CkModel>>();
        A.CallTo(() => resultSet.Items).ReturnsLazily(() => _installed.ToArray());
        var repository = A.Fake<ITenantRepository>();
        A.CallTo(() => repository.GetCkModelsAsync(A<IOctoSession>._, A<List<CkModelId>?>._, A<RtEntityQueryOptions>._,
            A<int?>._, A<int?>._)).Returns(resultSet);
        A.CallTo(() => _tenantContext.GetTenantRepository()).Returns(repository);
        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.FindTenantContextAsync(TenantId)).Returns(_tenantContext);

        A.CallTo(() => _catalogService.ListAsync(A<int>._, A<int>._, A<object?>._, A<CancellationToken?>._))
            .ReturnsLazily(() => new ModelListResult
            {
                TotalCount = _offered.Count, SkippedCount = 0, TakeCount = 500,
                ModelResultItems = _offered.Select(o => new CatalogResultItem
                    { ModelId = o, CatalogName = "Test", Description = o.Name }).ToList()
            });
        A.CallTo(() => _catalogService.GetAsync(A<CkModelId>._, A<OperationResult>._, A<object?>._,
                A<CancellationToken?>._))
            .ReturnsLazily((CkModelId id, OperationResult _, object? _, CancellationToken? _) =>
                Task.FromResult<CkCompiledModelRoot?>(new CkCompiledModelRoot { ModelId = id, Dependencies = [] }));

        A.CallTo(() => _tenantContext.EvaluateCkUpdateAsync(A<IReadOnlyCollection<CkCompiledModelRoot>>._,
                A<IReadOnlyCollection<CkModelId>?>._, A<CancellationToken>._))
            .ReturnsLazily((IReadOnlyCollection<CkCompiledModelRoot> set, IReadOnlyCollection<CkModelId>? available,
                CancellationToken _) =>
            {
                _updateSetsEvaluated.Add(set.Select(c => c.ModelId).ToList());
                if (_guardIsClear(set))
                {
                    return Task.FromResult(CkUpgradeGuardResult.None);
                }

                // Registration 2.8.0 is exact-pinned to the installed version of every candidate of the set.
                var registration = _installed.Single(m => m.ModelId == Registration);
                var blockers = set
                    .Where(c => c.ModelId.Name is "Basic" or "Basic.Energy" or "EnergyCommunity")
                    .Select(c =>
                    {
                        var pin = _installed.Single(m => m.ModelId == c.ModelId.Name).Id;
                        return new CkUpgradeBlocker(registration.Id, c.ModelId, CkUpgradeBlockerKind.ExactPin,
                            pin.FullName, "CK_UPGRADE_EXACT_PIN",
                            $"exact pin {pin.FullName}: {c.ModelId.FullName} replaces it", false,
                            available?.Where(a => a.Name == Registration && a.Version.CompareTo(registration.Id.Version) > 0)
                                .OrderByDescending(a => a.Version).FirstOrDefault());
                    }).ToList();
                return Task.FromResult(new CkUpgradeGuardResult(blockers, []));
            });

        return new CkModelLibraryStatusService(_catalogService, systemContext);
    }

    [Fact]
    public async Task LibraryStatus_SbegCase_BlocksTheThreeUpdatesAndKeepsHasUpdate()
    {
        SeedSbeg();

        var status = await CreateSut().GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        foreach (var name in new[] { "Basic", "Basic.Energy", "EnergyCommunity" })
        {
            var row = status.Items.Single(i => i.Name == name);
            row.IsBlocked.Should().BeTrue(name);
            row.HasUpdate.Should().BeTrue(name);
            row.NeedsAction.Should().BeFalse(name);
            var blocker = row.Blockers.Should().ContainSingle().Subject;
            blocker.Dependent.Should().Be("EnergyCommunity.Registration-2.8.0");
            blocker.Message.Should().Contain("EnergyCommunity.Registration 2.8.0")
                .And.Contain($"exact pin {name}-");
            blocker.Kind.Should().Be("ExactPin");
            blocker.Code.Should().Be("CK_UPGRADE_EXACT_PIN");
            blocker.Update.Should().Be(row.FullModelId);
            blocker.ResolveHint.Should().BeNull("the catalog offers no newer Registration");
        }

        // The pin of the Basic row names the installed Basic version.
        status.Items.Single(i => i.Name == "Basic").Blockers[0].Subject.Should().Be("Basic-2.4.0");
        status.ModelsNeedingActionCount.Should().Be(0);
    }

    [Fact]
    public async Task LibraryStatus_AdditiveUpdate_StaysAvailable()
    {
        // The same tenant, but Registration is range-retaining: nothing breaks, the guard is clear.
        SeedSbeg();
        _guardIsClear = _ => true;

        var status = await CreateSut().GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        var basic = status.Items.Single(i => i.Name == "Basic");
        basic.IsBlocked.Should().BeFalse();
        basic.Blockers.Should().BeEmpty();
        basic.HasUpdate.Should().BeTrue();
        basic.NeedsAction.Should().BeTrue();
        status.ModelsNeedingActionCount.Should().Be(3);
    }

    [Fact]
    public async Task LibraryStatus_OfferedNewerDependent_GetsAProvenResolveHint()
    {
        SeedSbeg();
        _offered.Add(Id(Registration, "2.10.0"));
        // Proof: the combined set {candidate, Registration-2.10.0} is clear.
        _guardIsClear = set => set.Any(c => c.ModelId.FullName == "EnergyCommunity.Registration-2.10.0");

        var status = await CreateSut().GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        var basic = status.Items.Single(i => i.Name == "Basic");
        basic.IsBlocked.Should().BeTrue();
        var blocker = basic.Blockers.Single();
        blocker.SuggestedDependentUpdate.Should().Be("EnergyCommunity.Registration-2.10.0");
        blocker.ResolveHint.Should().Be("update EnergyCommunity.Registration to 2.10.0 first");
        // The proof evaluated the combined update set, not just the single candidate.
        _updateSetsEvaluated.Should().Contain(s =>
            s.Select(i => i.FullName).OrderBy(x => x).SequenceEqual(new[]
                { "Basic-2.5.0", "EnergyCommunity.Registration-2.10.0" }));
        // Registration itself is updatable: the guard is clear for it alone.
        status.Items.Single(i => i.Name == Registration).IsBlocked.Should().BeFalse();
    }

    [Fact]
    public async Task LibraryStatus_SuggestionThatDoesNotResolveTheBlocker_GetsNoHint()
    {
        SeedSbeg();
        _offered.Add(Id(Registration, "2.10.0"));
        // The combined set still blocks (Registration 2.10.0 is still exact-pinned to the old Basic).
        _guardIsClear = _ => false;

        var status = await CreateSut().GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        var blocker = status.Items.Single(i => i.Name == "Basic").Blockers.Single();
        blocker.SuggestedDependentUpdate.Should().BeNull();
        blocker.ResolveHint.Should().BeNull();
    }

    [Fact]
    public async Task LibraryStatus_OnceTheDependentIsUpdated_TheBasicRowIsNoLongerBlocked()
    {
        SeedSbeg();
        _installed.RemoveAll(m => m.ModelId == Registration);
        _installed.Add(Installed(Registration, "2.10.0", rangeRetaining: true));
        _guardIsClear = _ => true;

        var status = await CreateSut().GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        status.Items.Single(i => i.Name == "Basic").IsBlocked.Should().BeFalse();
        status.Items.Single(i => i.Name == Registration).IsRangeRetaining.Should().BeTrue();
    }

    [Fact]
    public async Task LibraryStatus_ServiceManagedAndRowsWithoutUpdate_AreNeverEvaluated()
    {
        _installed.AddRange([Installed("System", "2.0.0"), Installed("Current", "1.0.0"), Installed(Registration, "2.8.0")]);
        _offered.AddRange([Id("System", "2.1.0"), Id("Current", "1.0.0"), Id("NewOnly", "1.0.0")]);

        var status = await CreateSut().GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        status.Items.Should().OnlyContain(i => !i.IsBlocked && i.Blockers.Count == 0);
        A.CallTo(() => _tenantContext.EvaluateCkUpdateAsync(A<IReadOnlyCollection<CkCompiledModelRoot>>._,
            A<IReadOnlyCollection<CkModelId>?>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task LibraryStatus_GuardFailureOfOneRow_YieldsThatRowsErrorStateOnly()
    {
        SeedSbeg();
        _guardIsClear = _ => true;
        var sut = CreateSut();
        A.CallTo(() => _tenantContext.EvaluateCkUpdateAsync(
                A<IReadOnlyCollection<CkCompiledModelRoot>>.That.Matches(s => s.Any(c => c.ModelId.Name == "Basic.Energy")),
                A<IReadOnlyCollection<CkModelId>?>._, A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("guard exploded"));

        var status = await sut.GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        var failed = status.Items.Single(i => i.Name == "Basic.Energy");
        failed.IsCompatible.Should().BeFalse();
        failed.IncompatibilityReason.Should().Contain("guard exploded");
        failed.NeedsAction.Should().BeFalse();
        status.Items.Single(i => i.Name == "Basic").NeedsAction.Should().BeTrue();
    }

    [Fact]
    public async Task LibraryStatus_IsRangeRetaining_DistinguishesRangeRetainingFromExactPinnedModels()
    {
        _installed.AddRange([
            Installed("Ranged", "1.0.0", rangeRetaining: true), Installed("Pinned", "1.0.0")
        ]);

        var status = await CreateSut().GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        status.Items.Single(i => i.Name == "Ranged").IsRangeRetaining.Should().BeTrue();
        status.Items.Single(i => i.Name == "Pinned").IsRangeRetaining.Should().BeFalse();
    }

    [Fact]
    public async Task EvaluateUpgradeBlockers_ReturnsTheBlockersOfTheCandidate_AndWritesNothing()
    {
        SeedSbeg();

        var blockers = await CreateSut().EvaluateUpgradeBlockersAsync(TenantId, Id("Basic", "2.5.0"),
            TestContext.Current.CancellationToken);

        blockers.Should().ContainSingle().Which.Dependent.Should().Be("EnergyCommunity.Registration-2.8.0");
        A.CallTo(() => _tenantContext.ImportCkModelAsync(A<CkCompiledModelRoot>._)).MustNotHaveHappened();
    }
}
