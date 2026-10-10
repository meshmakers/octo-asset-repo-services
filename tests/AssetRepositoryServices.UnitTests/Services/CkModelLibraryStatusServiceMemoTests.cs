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
/// AB#6330: one library-status evaluation fetches each catalog model at most once, however many rows
/// share it as a dependency, and the per-row results stay unchanged.
/// </summary>
public class CkModelLibraryStatusServiceMemoTests
{
    private const string TenantId = "test-tenant";

    private readonly ICatalogService _catalogService = A.Fake<ICatalogService>();
    private readonly CkModelLibraryStatusService _sut;
    private readonly List<string> _fetched = [];

    public CkModelLibraryStatusServiceMemoTests()
    {
        var installed = A.Fake<IResultSet<CkModel>>();
        A.CallTo(() => installed.Items).Returns(Array.Empty<CkModel>());
        var repository = A.Fake<ITenantRepository>();
        A.CallTo(() => repository.GetCkModelsAsync(A<IOctoSession>._, A<List<CkModelId>?>._, A<RtEntityQueryOptions>._,
                A<int?>._, A<int?>._))
            .Returns(installed);
        var tenantContext = A.Fake<ITenantContext>();
        A.CallTo(() => tenantContext.GetTenantRepository()).Returns(repository);
        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.FindTenantContextAsync(TenantId)).Returns(tenantContext);

        A.CallTo(() => _catalogService.GetAsync(A<CkModelId>._, A<OperationResult>._, A<object?>._,
                A<CancellationToken?>._))
            .ReturnsLazily((CkModelId id, OperationResult _, object? _, CancellationToken? _) =>
            {
                Task<CkCompiledModelRoot?> Ok(CkCompiledModelRoot m) => Task.FromResult<CkCompiledModelRoot?>(m);
                _fetched.Add(id.FullName);
                return id.Name switch
                {
                    "Missing" => throw ModelCatalogException.ModelNotFound(id, "Test"),
                    "Basic" => Ok(Compiled(id)),
                    "Leaf" => Ok(Compiled(id)),
                    _ => Ok(Compiled(id, new CkModelId("Basic", "1.0.0"),
                        new CkModelId("Missing", "1.0.0")))
                };
            });

        _sut = new CkModelLibraryStatusService(_catalogService, systemContext);
    }

    private static CkCompiledModelRoot Compiled(CkModelId id, params CkModelId[] deps) =>
        new() { ModelId = id, Dependencies = deps.ToList() };

    private void SetCatalog(params string[] names)
    {
        var result = new ModelListResult
        {
            TotalCount = names.Length, SkippedCount = 0, TakeCount = 500,
            ModelResultItems = names.Select(n => new CatalogResultItem
                { ModelId = new CkModelId(n, "1.0.0"), CatalogName = "Test", Description = n }).ToList()
        };
        A.CallTo(() => _catalogService.ListAsync(A<int>._, A<int>._, A<object?>._, A<CancellationToken?>._))
            .Returns(result);
    }

    [Fact]
    public async Task GetLibraryStatusAsync_SharedDependency_IsFetchedOncePerEvaluation()
    {
        SetCatalog("A", "B", "C", "Basic");

        var status = await _sut.GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        status.Items.Should().HaveCount(4);
        _fetched.Should().OnlyHaveUniqueItems();
        _fetched.Should().BeEquivalentTo("A-1.0.0", "B-1.0.0", "C-1.0.0", "Basic-1.0.0", "Missing-1.0.0");
    }

    [Fact]
    public async Task GetLibraryStatusAsync_UnresolvedDependency_IsReportedOnEveryRowThatNeedsIt()
    {
        SetCatalog("A", "B", "Leaf");

        var status = await _sut.GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        foreach (var name in new[] { "A", "B" })
        {
            var row = status.Items.Single(i => i.Name == name);
            row.HasCatalogInconsistency.Should().BeTrue();
            row.IsCompatible.Should().BeFalse();
            row.UnresolvedDependencies.Should().Equal("Missing-1.0.0");
        }

        var leaf = status.Items.Single(i => i.Name == "Leaf");
        leaf.HasCatalogInconsistency.Should().BeFalse();
        leaf.IsCompatible.Should().BeTrue();
        _fetched.Count(f => f == "Missing-1.0.0").Should().Be(1);
    }

    [Fact]
    public async Task GetLibraryStatusAsync_SeparateEvaluations_DoNotShareTheMemo()
    {
        SetCatalog("Leaf");

        await _sut.GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);
        await _sut.GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        _fetched.Should().Equal("Leaf-1.0.0", "Leaf-1.0.0");
    }
}
