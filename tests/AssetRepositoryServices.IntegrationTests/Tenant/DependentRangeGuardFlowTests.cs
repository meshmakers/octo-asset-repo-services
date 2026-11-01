using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Tenant;

/// <summary>
/// AB#7998: the EnergyIQ / Basic.Energy case against the real service, tenant context, engine guard and database.
/// <c>DrApp-1.0.0</c> is range-retaining on <c>DrLib-[1.0,2.0)</c>, <c>DrPinned-1.0.0</c> is exact-pinned to
/// <c>DrLib-1.0.0</c>. The catalog offers <c>DrLib-1.1.0</c> and <c>DrLib-2.0.0</c>. The next major is refused (naming the
/// dependent), LibraryStatus offers the highest allowed version and names the held-back one; nothing is written.
/// </summary>
[Collection(BlueprintUpdateFlowCollection.Name)]
public class DependentRangeGuardFlowTests(BlueprintUpdateFlowFixture fixture)
{
    private const int TestTimeoutMs = 180_000;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task NextMajorPastARange_IsBlocked_AndTheRowOffersTheHighestAllowedVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await fixture.CreateTenantAsync("dr-rng");
        try
        {
            var systemContext = fixture.GetSystemContext();
            var tenant = await systemContext.FindTenantContextAsync(tenantId);
            var system = await InstalledSystemAsync(tenant);
            var systemRange = new CkModelDependencyDto
            {
                Range = new CkModelIdVersionRange("System",
                    $"[{system.Version.Major}.{system.Version.Minor},{system.Version.Major + 1}.0)"),
                Floor = system.Version.ToString()
            };

            await tenant.ImportCkModelAsync(Lib("1.0.0", system));
            await tenant.ImportCkModelAsync(new CkCompiledModelRoot
            {
                ModelId = new CkModelId("DrApp-1.0.0"), Dependencies = [new CkModelId("DrLib-1.0.0"), system],
                DependencyRanges =
                [
                    systemRange,
                    new CkModelDependencyDto { Range = new CkModelIdVersionRange("DrLib-[1.0,2.0)"), Floor = "1.0.0" }
                ],
                Types = []
            });

            var offered = new[] { Lib("1.0.0", system), Lib("1.1.0", system), Lib("2.0.0", system) };
            var sut = new CkModelLibraryStatusService(Catalog(offered), systemContext);

            // Single import of the next major: refused, the dependent and its range are named.
            var blockers = await sut.EvaluateUpdateSetBlockersAsync(tenantId, [new CkModelId("DrLib-2.0.0")], ct);
            var blocker = blockers.Should().ContainSingle(b => b.Dependent == "DrApp-1.0.0").Subject;
            blocker.Subject.Should().Be("DrLib-[1.0,2.0)");
            blocker.Code.Should().Be("CK_UPGRADE_RANGE_NOT_SATISFIED");

            // Inside the range: allowed.
            (await sut.EvaluateUpdateSetBlockersAsync(tenantId, [new CkModelId("DrLib-1.1.0")], ct))
                .Should().BeEmpty();

            // The row (what FixAll imports) offers 1.1.0 and names 2.0.0 as held back by DrApp.
            var status = await sut.GetLibraryStatusAsync(tenantId, ct);
            var row = status.Items.Single(i => i.Name == "DrLib");
            row.FullModelId.Should().Be("DrLib-1.1.0");
            row.NeedsAction.Should().BeTrue();
            row.IsBlocked.Should().BeFalse();
            row.HeldBackVersion.Should().Be("2.0.0");
            row.HeldBackReason.Should().Be("2.0.0 available, held back by DrApp 1.0.0 (DrLib-[1.0,2.0))");

            // Exact-pinned dependent: every other version is held back.
            await tenant.ImportCkModelAsync(new CkCompiledModelRoot
            {
                ModelId = new CkModelId("DrPinned-1.0.0"), Dependencies = [new CkModelId("DrLib-1.0.0"), system],
                Types = []
            });
            status = await sut.GetLibraryStatusAsync(tenantId, ct);
            row = status.Items.Single(i => i.Name == "DrLib");
            row.NeedsAction.Should().BeFalse();
            row.IsBlocked.Should().BeTrue();
            row.HeldBackBy.Should().Contain(b => b.Dependent == "DrPinned-1.0.0" && b.Subject == "DrLib-1.0.0");

            // Nothing was written: DrLib is still 1.0.0, both dependents are installed.
            (await tenant.IsCkModelExistingAsync(new CkModelId("DrLib-1.0.0"))).Should().BeTrue();
            (await tenant.IsCkModelExistingAsync(new CkModelId("DrLib-2.0.0"))).Should().BeFalse();
            (await tenant.IsCkModelExistingAsync(new CkModelId("DrApp-1.0.0"))).Should().BeTrue();
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    private static CkCompiledModelRoot Lib(string version, CkModelId system) =>
        new() { ModelId = new CkModelId($"DrLib-{version}"), Dependencies = [system], Types = [] };

    private static ICatalogService Catalog(IReadOnlyCollection<CkCompiledModelRoot> offered)
    {
        var catalog = A.Fake<ICatalogService>();
        A.CallTo(() => catalog.ListAsync(A<int>._, A<int>._, A<object?>._, A<CancellationToken?>._))
            .Returns(new ModelListResult
            {
                TotalCount = offered.Count, SkippedCount = 0, TakeCount = 500,
                ModelResultItems = offered.Select(m => new CatalogResultItem
                    { ModelId = m.ModelId, CatalogName = "Test", Description = m.ModelId.Name }).ToList()
            });
        A.CallTo(() => catalog.GetAsync(A<CkModelId>._, A<OperationResult>._, A<object?>._, A<CancellationToken?>._))
            .ReturnsLazily((CkModelId id, OperationResult _, object? _, CancellationToken? _) =>
                offered.FirstOrDefault(m => m.ModelId.FullName == id.FullName) is { } m
                    ? Task.FromResult<CkCompiledModelRoot?>(m)
                    : throw ModelCatalogException.ModelNotFound(id, "Test"));
        return catalog;
    }

    private async Task<CkModelId> InstalledSystemAsync(Meshmakers.Octo.Runtime.Contracts.MongoDb.ITenantContext tenant)
    {
        var catalogService = fixture.GetService<ICatalogService>();
        foreach (var version in (await catalogService.ListVersionsAsync("System")).OrderByDescending(v => v.ModelId))
        {
            if (await tenant.IsCkModelExistingAsync(version.ModelId))
            {
                return version.ModelId;
            }
        }

        throw new InvalidOperationException("No installed System model found in the catalogs.");
    }
}
