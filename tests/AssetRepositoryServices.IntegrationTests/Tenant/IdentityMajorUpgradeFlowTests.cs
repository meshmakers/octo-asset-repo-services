using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Tenant;

/// <summary>
///     AB#8053: an existing tenant that holds <c>System.Identity-2.30.0</c> (compiler output of the last 2.x release,
///     <c>testData/ck-models/ck-system.identity-2.30.0.yaml</c>) is upgraded to the embedded <c>System.Identity-3.0.0</c> by the
///     same import every service runs on tenant resolve: the 2.30.0 row is replaced, the stored user keeps all values, the
///     rebuilt CK cache carries <c>MethodOnly</c> and <c>replacesGenericCreate</c>, and a blueprint with the new
///     <c>System.Identity-[3.0,4.0)</c> range applies on the upgraded tenant. No data migration is involved.
/// </summary>
[Collection(IdentitySeedBlueprintCollection.Name)]
public class IdentityMajorUpgradeFlowTests(IdentitySeedBlueprintFixture fixture)
{
    [Fact(Timeout = 240_000)]
    public async Task TenantOnSystemIdentity230_UpgradesToTheEmbedded300_KeepingItsData()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(AppContext.BaseDirectory, "testData", "ck-models", "ck-system.identity-2.30.0.yaml");
        var tenantId = await fixture.CreateTenantAsync("idup");
        try
        {
            var tenant = await fixture.GetSystemContext().FindTenantContextAsync(tenantId);
            var yaml = fixture.GetService<ICkYamlSerializer>();
            var op = new OperationResult();
            await using (var fs = File.OpenRead(path))
            {
                var root = await yaml.DeserializeCompiledModelRootAsync(fs, path, op);
                await tenant.ImportCkModelAsync(root);
            }

            (await tenant.IsCkModelExistingAsync(new CkModelId("System.Identity-2.30.0"))).Should().BeTrue();

            var userType = new RtCkId<CkTypeId>("System.Identity/User");
            var rtId = OctoObjectId.GenerateNewId();
            var repository = await fixture.GetRuntimeRepositoryProvider().GetRepositoryAsync(tenantId, ct);
            using (var session = await repository!.GetSessionAsync())
            {
                session.StartTransaction();
                var user = new RtEntity(userType, rtId);
                user.SetAttributeRawValue("FirstName", "Upgrade");
                user.SetAttributeRawValue("TwoFactorEnabled", true);
                user.SetAttributeRawValue("AccessFailedCount", 3);
                await repository.InsertOneRtEntityAsync(session, user);
                await session.CommitTransactionAsync();
            }

            var op2 = new OperationResult();
            await tenant.ImportCkModelAsync(new CkModelId("System.Identity-3.0.0"), op2);
            op2.Messages.Should().BeEmpty(string.Join("; ", op2.Messages.Select(m => m.MessageText)));
            (await tenant.IsCkModelExistingAsync(new CkModelId("System.Identity-3.0.0"))).Should().BeTrue();
            (await tenant.IsCkModelExistingAsync(new CkModelId("System.Identity-2.30.0"))).Should().BeFalse();

            // cache rebuilt from what is installed: access is MethodOnly now, data untouched
            var repository2 = await fixture.GetRuntimeRepositoryProvider().GetRepositoryAsync(tenantId, ct);
            var graph = await repository2!.GetCkTypeGraphAsync(userType);
            graph.AllAttributes.Values.Single(a => a.AttributeName == "TwoFactorEnabled").Access
                .Should().Be(CkAttributeAccessDto.MethodOnly);
            graph.AllMethods["CreateUser-1"].ReplacesGenericCreate.Should().BeTrue();
            using (var session = await repository2.GetSessionAsync())
            {
                var users = await repository2.GetRtEntitiesByTypeAsync(session, userType, RtEntityQueryOptions.Create());
                var u = users.Items.Single();
                u.RtId.Should().Be(rtId);
                u.GetAttributeValueOrDefault("FirstName")!.ToString().Should().Be("Upgrade");
                u.GetAttributeValueOrDefault<bool>("TwoFactorEnabled").Should().BeTrue();
                u.GetAttributeValueOrDefault<int>("AccessFailedCount").Should().Be(3);
            }

            // a blueprint with the new range applies on the upgraded tenant (and refuses a new user: 136)
            var result = await fixture.GetBlueprintService()
                .ApplyBlueprintAsync(tenantId, new BlueprintId("NewUserSeedBp-1.0.0"), false, ct);
            result.IsSuccess.Should().BeFalse();
            string.Join("; ", result.OperationResult.Messages.Select(m => $"[{m.MessageNumber}] {m.MessageText}"))
                .Should().Contain("[136]");
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }
}
