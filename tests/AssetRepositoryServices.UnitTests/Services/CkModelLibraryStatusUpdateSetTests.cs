using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.UpgradeGuard;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.UnitTests.Services;

/// <summary>
/// AB#7017: a library status row is judged with the dependency updates its import pulls in, not the offered version
/// alone - the same set the import dialog (ResolveDependencies) judges. The tenant upgrade guard is faked (its own
/// behaviour is covered in octo-construction-kit-engine-mongodb); these tests pin which set the status service hands to it.
/// Scenario (local proof of Daidalos C): Industry.Energy-3.6.0 brings Basic-2.5.0 with it, and
/// EnergyCommunity.Registration-2.8.0 is exact-pinned to the installed Basic-2.4.0.
/// </summary>
public class CkModelLibraryStatusUpdateSetTests
{
    private const string TenantId = "t1";
    private const string Registration = "EnergyCommunity.Registration";
    private const string CatalogName = "Test";

    private readonly ITenantContext _tenantContext = A.Fake<ITenantContext>();
    private readonly ICatalogService _catalogService = A.Fake<ICatalogService>();
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly List<CkModel> _installed = [];
    private readonly List<CkModelId> _offered = [];
    private readonly Dictionary<string, CkModelId[]> _dependenciesOfOffered = new();
    private readonly List<string[]> _setsEvaluated = [];

    private static CkModelId Id(string name, string version) => new(name, version);

    private static CkModel Installed(string name, string version) =>
        new() { Id = Id(name, version), ModelId = name, ModelState = ModelState.Available };

    private void SeedTenant(bool withRegistration = true, string basicVersion = "2.4.0")
    {
        _installed.AddRange([
            Installed("System", "2.0.0"), Installed("Basic", basicVersion),
            Installed("Industry.Energy", "3.5.0"), Installed("Standalone", "1.0.0")
        ]);
        if (withRegistration)
        {
            _installed.Add(Installed(Registration, "2.8.0"));
        }

        _offered.AddRange([Id("Industry.Energy", "3.6.0"), Id("Standalone", "1.1.0")]);
        _dependenciesOfOffered["Industry.Energy-3.6.0"] = [Id("Basic", "2.5.0")];
    }

    private CkModelLibraryStatusService CreateSut()
    {
        var resultSet = A.Fake<IResultSet<CkModel>>();
        A.CallTo(() => resultSet.Items).ReturnsLazily(() => _installed.ToArray());
        var repository = A.Fake<ITenantRepository>();
        A.CallTo(() => repository.GetCkModelsAsync(A<IOctoSession>._, A<List<CkModelId>?>._, A<RtEntityQueryOptions>._,
            A<int?>._, A<int?>._)).Returns(resultSet);
        A.CallTo(() => _tenantContext.GetTenantRepository()).Returns(repository);
        A.CallTo(() => _tenantContext.IsCkModelExistingAsync(A<CkModelId>._))
            .ReturnsLazily((CkModelId id) => Task.FromResult(_installed.Any(m =>
                m.Id.FullName == id.FullName && m.ModelState == ModelState.Available)));
        A.CallTo(() => _systemContext.FindTenantContextAsync(TenantId)).Returns(_tenantContext);

        A.CallTo(() => _catalogService.ListAsync(A<int>._, A<int>._, A<object?>._, A<CancellationToken?>._))
            .ReturnsLazily(() => new ModelListResult
            {
                TotalCount = _offered.Count, SkippedCount = 0, TakeCount = 500,
                ModelResultItems = _offered.Select(o => new CatalogResultItem
                    { ModelId = o, CatalogName = CatalogName, Description = o.Name }).ToList()
            });
        CkCompiledModelRoot Compiled(CkModelId id) => new()
        {
            ModelId = id,
            Dependencies = _dependenciesOfOffered.TryGetValue(id.FullName, out var deps) ? [.. deps] : []
        };
        A.CallTo(() => _catalogService.GetAsync(A<CkModelId>._, A<OperationResult>._, A<object?>._,
                A<CancellationToken?>._))
            .ReturnsLazily((CkModelId id, OperationResult _, object? _, CancellationToken? _) =>
                Task.FromResult<CkCompiledModelRoot?>(Compiled(id)));
        A.CallTo(() => _catalogService.GetAsync(CatalogName, A<CkModelId>._, A<OperationResult>._,
                A<CancellationToken?>._))
            .ReturnsLazily((string _, CkModelId id, OperationResult _, CancellationToken? _) =>
                Task.FromResult<CkCompiledModelRoot?>(Compiled(id)));

        // Registration is exact-pinned to the installed Basic: raising Basic breaks it, unless Registration itself is
        // raised in the same set. Without an installed Registration nothing breaks.
        A.CallTo(() => _tenantContext.EvaluateCkUpdateAsync(A<IReadOnlyCollection<CkCompiledModelRoot>>._,
                A<IReadOnlyCollection<CkModelId>?>._, A<CancellationToken>._))
            .ReturnsLazily((IReadOnlyCollection<CkCompiledModelRoot> set, IReadOnlyCollection<CkModelId>? _,
                CancellationToken _) =>
            {
                _setsEvaluated.Add(set.Select(c => c.ModelId.FullName).OrderBy(x => x).ToArray());
                var registration = _installed.SingleOrDefault(m => m.ModelId == Registration);
                var basic = _installed.Single(m => m.ModelId == "Basic");
                var raisedBasic = set.Select(c => c.ModelId).FirstOrDefault(c => c.Name == "Basic");
                if (registration == null || raisedBasic == null ||
                    raisedBasic.Version.CompareTo(basic.Id.Version) <= 0)
                {
                    return Task.FromResult(CkUpgradeGuardResult.None);
                }

                var blocker = new CkUpgradeBlocker(registration.Id, raisedBasic, CkUpgradeBlockerKind.ExactPin,
                    basic.Id.FullName, "CK_UPGRADE_EXACT_PIN",
                    $"exact pin {basic.Id.FullName}: {raisedBasic.FullName} replaces it", false, null);
                return Task.FromResult(new CkUpgradeGuardResult([blocker], []));
            });

        return new CkModelLibraryStatusService(_catalogService, _systemContext);
    }

    [Fact]
    public async Task Row_WhoseImportPullsInABreakingDependencyUpdate_IsBlockedWithTheDependencyAsCause()
    {
        SeedTenant();

        var status = await CreateSut().GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        var row = status.Items.Single(i => i.Name == "Industry.Energy");
        row.IsBlocked.Should().BeTrue();
        row.HasUpdate.Should().BeTrue();
        row.NeedsAction.Should().BeFalse("FixAll must skip a blocked row");
        var blocker = row.Blockers.Should().ContainSingle().Subject;
        blocker.Update.Should().Be("Basic-2.5.0");
        blocker.Dependent.Should().Be("EnergyCommunity.Registration-2.8.0");
        blocker.Subject.Should().Be("Basic-2.4.0");
        // The guard was asked about the combined set, dependencies first.
        _setsEvaluated.Should().Contain(s => s.SequenceEqual(new[] { "Basic-2.5.0", "Industry.Energy-3.6.0" }));
    }

    [Fact]
    public async Task Row_WhoseCombinedSetIsSafe_StaysUnblocked()
    {
        // No installed model depends on the old Basic, so raising it breaks nothing.
        SeedTenant(withRegistration: false);

        var status = await CreateSut().GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        var row = status.Items.Single(i => i.Name == "Industry.Energy");
        row.IsBlocked.Should().BeFalse();
        row.Blockers.Should().BeEmpty();
        row.NeedsAction.Should().BeTrue();
        row.HasUpdate.Should().BeTrue();
    }

    [Fact]
    public async Task Row_WhoseDependenciesAreAlreadySatisfied_IsJudgedByItsOwnVersionOnly()
    {
        // Basic-2.5.0 is installed already: the import pulls in nothing, the row is judged as before.
        SeedTenant(basicVersion: "2.5.0");

        var status = await CreateSut().GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        status.Items.Single(i => i.Name == "Industry.Energy").IsBlocked.Should().BeFalse();
        _setsEvaluated.Should().Contain(s => s.SequenceEqual(new[] { "Industry.Energy-3.6.0" }));
        _setsEvaluated.Should().NotContain(s => s.Contains("Basic-2.5.0"));
    }

    [Fact]
    public async Task Row_WithoutDependencyUpdates_IsJudgedAsToday()
    {
        SeedTenant();

        var status = await CreateSut().GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        var row = status.Items.Single(i => i.Name == "Standalone");
        row.IsBlocked.Should().BeFalse();
        row.NeedsAction.Should().BeTrue();
        _setsEvaluated.Should().Contain(s => s.SequenceEqual(new[] { "Standalone-1.1.0" }));
    }

    [Fact]
    public async Task RowsSharingADependency_FetchEachCatalogModelOnce_AndAskTheTenantNothingMore()
    {
        SeedTenant();
        _offered.Add(Id("Second", "2.0.0"));
        _installed.Add(Installed("Second", "1.0.0"));
        _dependenciesOfOffered["Second-2.0.0"] = [Id("Basic", "2.5.0")];

        await CreateSut().GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        A.CallTo(() => _catalogService.GetAsync(A<CkModelId>.That.Matches(m => m.FullName == "Basic-2.5.0"),
                A<OperationResult>._, A<object?>._, A<CancellationToken?>._))
            .MustHaveHappenedOnceExactly();
        // The installed state is read once with the status call; no lookup per dependency.
        A.CallTo(() => _tenantContext.IsCkModelExistingAsync(A<CkModelId>._)).MustNotHaveHappened();
    }

    // ---- AB#7029: the blueprint preview/apply judges its CK models together with the dependency updates they pull in ----

    [Fact]
    public async Task BlueprintSet_WhoseModelPullsInABreakingDependencyUpdate_IsBlockedWithTheDependencyAsCause()
    {
        SeedTenant();

        // The blueprint declares only Industry.Energy at 3.6.0; Basic-2.5.0 comes with its import.
        var blockers = await CreateSut().EvaluateImportSetBlockersAsync(TenantId, [Id("Industry.Energy", "3.6.0")],
            TestContext.Current.CancellationToken);

        var blocker = blockers.Should().ContainSingle().Subject;
        blocker.Update.Should().Be("Basic-2.5.0");
        blocker.Dependent.Should().Be("EnergyCommunity.Registration-2.8.0");
        blocker.Subject.Should().Be("Basic-2.4.0");
        _setsEvaluated.Should().Contain(s => s.SequenceEqual(new[] { "Basic-2.5.0", "Industry.Energy-3.6.0" }));
    }

    [Fact]
    public async Task BlueprintSet_WhoseCombinedSetIsSafe_IsNotBlocked()
    {
        SeedTenant(withRegistration: false);

        var blockers = await CreateSut().EvaluateImportSetBlockersAsync(TenantId, [Id("Industry.Energy", "3.6.0")],
            TestContext.Current.CancellationToken);

        blockers.Should().BeEmpty();
    }

    [Fact]
    public async Task BlueprintSet_WithoutDependencyUpdates_IsJudgedAsToday()
    {
        SeedTenant();

        var blockers = await CreateSut().EvaluateImportSetBlockersAsync(TenantId, [Id("Standalone", "1.1.0")],
            TestContext.Current.CancellationToken);

        blockers.Should().BeEmpty();
        _setsEvaluated.Should().ContainSingle().Which.Should().Equal("Standalone-1.1.0");
    }

    [Fact]
    public async Task BlueprintSet_DeclaringTheDependencyItself_JudgesItOnce()
    {
        SeedTenant();

        await CreateSut().EvaluateImportSetBlockersAsync(TenantId,
            [Id("Basic", "2.5.0"), Id("Industry.Energy", "3.6.0")], TestContext.Current.CancellationToken);

        _setsEvaluated.Should().Contain(s => s.SequenceEqual(new[] { "Basic-2.5.0", "Industry.Energy-3.6.0" }));
        _setsEvaluated.Should().NotContain(s => s.Count(x => x.StartsWith("Basic-", StringComparison.Ordinal)) > 1);
    }

    /// <summary>The blueprint verdict is the verdict of the import dialog for the same model, for every state.</summary>
    [Theory]
    [InlineData("sbeg")]
    [InlineData("no-registration")]
    [InlineData("basic-already-raised")]
    [InlineData("registration-updated-too")]
    public async Task BlueprintVerdict_EqualsTheImportDialogVerdict_ForTheSameModel(string state)
    {
        switch (state)
        {
            case "sbeg":
                SeedTenant();
                break;
            case "no-registration":
                SeedTenant(withRegistration: false);
                break;
            case "basic-already-raised":
                SeedTenant(basicVersion: "2.5.0");
                break;
            case "registration-updated-too":
                SeedTenant();
                _offered.Add(Id(Registration, "2.10.0"));
                _dependenciesOfOffered["Industry.Energy-3.6.0"] = [Id("Basic", "2.5.0"), Id(Registration, "2.10.0")];
                break;
        }

        var sut = CreateSut();
        var blueprint = await sut.EvaluateImportSetBlockersAsync(TenantId, [Id("Industry.Energy", "3.6.0")],
            TestContext.Current.CancellationToken);

        var result = await CreateController(sut).ResolveDependencies(
            new ImportFromCatalogRequestDto { CatalogName = CatalogName, ModelId = "Industry.Energy-3.6.0" },
            TestContext.Current.CancellationToken);
        var dialog = (DependencyResolutionResponseDto)result.Should().BeOfType<OkObjectResult>().Subject.Value!;

        blueprint.Select(b => (b.Dependent, b.Update, b.Kind, b.Subject))
            .Should().BeEquivalentTo(dialog.Blockers.Select(b => (b.Dependent, b.Update, b.Kind, b.Subject)), state);
    }

    public static TheoryData<string> States =>
        ["sbeg", "no-registration", "basic-already-raised", "registration-updated-too"];

    /// <summary>
    /// The row and the import dialog are one verdict: for every state the row of the candidate equals what
    /// <c>ResolveDependencies</c> (the dialog, judged by EvaluateUpdateSetBlockersAsync) reports for the same candidate.
    /// </summary>
    [Theory]
    [MemberData(nameof(States))]
    public async Task RowVerdict_EqualsTheImportDialogVerdict_ForTheSameCandidate(string state)
    {
        switch (state)
        {
            case "sbeg":
                SeedTenant();
                break;
            case "no-registration":
                SeedTenant(withRegistration: false);
                break;
            case "basic-already-raised":
                SeedTenant(basicVersion: "2.5.0");
                break;
            case "registration-updated-too":
                SeedTenant();
                // Registration is offered newer as well and is pulled into the import by the candidate.
                _offered.Add(Id(Registration, "2.10.0"));
                _dependenciesOfOffered["Industry.Energy-3.6.0"] = [Id("Basic", "2.5.0"), Id(Registration, "2.10.0")];
                break;
        }

        var sut = CreateSut();
        var status = await sut.GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);
        var row = status.Items.Single(i => i.Name == "Industry.Energy");

        var controller = CreateController(sut);
        var result = await controller.ResolveDependencies(
            new ImportFromCatalogRequestDto { CatalogName = CatalogName, ModelId = "Industry.Energy-3.6.0" },
            TestContext.Current.CancellationToken);
        var dialog = (DependencyResolutionResponseDto)result.Should().BeOfType<OkObjectResult>().Subject.Value!;

        row.IsBlocked.Should().Be(dialog.IsBlocked, state);
        row.Blockers.Select(b => (b.Dependent, b.Update, b.Kind, b.Subject))
            .Should().BeEquivalentTo(dialog.Blockers.Select(b => (b.Dependent, b.Update, b.Kind, b.Subject)), state);
    }

    private ModelsController CreateController(ICkModelLibraryStatusService libraryStatusService)
    {
        var controller = new ModelsController(
            A.Fake<IDistributedCacheService>(),
            A.Fake<ICommandClient<ExportRtByQueryCommandRequest>>(),
            A.Fake<ICommandClient<ExportRtByDeepGraphCommandRequest>>(),
            A.Fake<ICommandClient<ImportRtCommandRequest>>(),
            A.Fake<ICommandClient<ImportCkCommandRequest>>(),
            A.Fake<ICommandClient<ImportCkBatchCommandRequest>>(),
            _catalogService,
            A.Fake<ICkJsonSerializer>(),
            _systemContext,
            A.Fake<Meshmakers.Octo.Runtime.Contracts.CkModelMigrations.ICkModelUpgradeService>(),
            A.Fake<Meshmakers.Octo.Runtime.Contracts.CkModelMigrations.ICkModelMigrationService>(),
            libraryStatusService,
            A.Fake<ILogger<ModelsController>>(),
            A.Fake<ICkSerializer>(),
            A.Fake<Microsoft.AspNetCore.Authorization.IAuthorizationService>());
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }
}
