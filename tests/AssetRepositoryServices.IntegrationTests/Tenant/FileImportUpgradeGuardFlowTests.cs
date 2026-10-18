using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Tenant;

/// <summary>
/// AB#6966 (CK v2 F2.5): the guard of the file import, against the real service, tenant context and database. A
/// <c>System</c> file whose version replaces the installed one is judged (the service-managed skip of the default paths
/// does not apply to an explicit caller) and reports the model that is exact-pinned to the installed System; the
/// default path and the evaluation itself write nothing, and the tenant still loads.
/// </summary>
[Collection(BlueprintUpdateFlowCollection.Name)]
public class FileImportUpgradeGuardFlowTests(BlueprintUpdateFlowFixture fixture)
{
    private const int TestTimeoutMs = 180_000;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SystemFile_IsJudgedAsAnExplicitCaller_TheDefaultPathSkipsIt_AndNothingIsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await fixture.CreateTenantAsync("fi-ckg");
        try
        {
            var systemContext = fixture.GetSystemContext();
            var tenant = await systemContext.FindTenantContextAsync(tenantId);
            var installedSystem = await InstalledSystemAsync(tenant);
            await tenant.ImportCkModelAsync(new CkCompiledModelRoot
            {
                ModelId = new CkModelId("FiBase-1.0.0"), Dependencies = [installedSystem], Types = []
            });

            var catalog = A.Fake<ICatalogService>();
            A.CallTo(() => catalog.ListAsync(A<int>._, A<int>._, A<object?>._, A<CancellationToken?>._))
                .Returns(new ModelListResult { ModelResultItems = [], TotalCount = 0, SkippedCount = 0, TakeCount = 500 });
            var sut = new CkModelLibraryStatusService(catalog, systemContext);
            var newerSystem = new CkCompiledModelRoot
            {
                ModelId = new CkModelId("System",
                    new CkVersion(installedSystem.Version.Major, installedSystem.Version.Minor + 1, 0).ToString()),
                Types = []
            };

            var blockers = await sut.EvaluateFileImportBlockersAsync(tenantId, [newerSystem], ct);

            var blocker = blockers.Should().Contain(b => b.Dependent == "FiBase-1.0.0").Subject;
            blocker.Kind.Should().Be("ExactPin");
            blocker.Update.Should().Be(newerSystem.ModelId.FullName);
            blocker.Code.Should().Be("CK_UPGRADE_EXACT_PIN");

            // The default path (blueprints, FixAll, batch import) never judges System.
            (await tenant.EvaluateCkUpdateAsync([newerSystem], null, ct)).Blockers.Should().BeEmpty();

            // Nothing was imported: System is still the installed version, FiBase is Available, the tenant still loads.
            (await InstalledSystemAsync(await systemContext.FindTenantContextAsync(tenantId))).Should().Be(installedSystem);
            (await tenant.IsCkModelExistingAsync(new CkModelId("FiBase-1.0.0"))).Should().BeTrue();
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
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
