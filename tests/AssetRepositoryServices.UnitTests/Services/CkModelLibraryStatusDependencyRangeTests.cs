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
        var repository = A.Fake<ITenantRepository>();
        A.CallTo(() => repository.GetCkModelsAsync(A<IOctoSession>._, A<List<CkModelId>?>._, A<RtEntityQueryOptions>._,
                A<int?>._, A<int?>._)).Returns(installed);
        var tenantContext = A.Fake<ITenantContext>();
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
}
