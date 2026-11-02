using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Security;

/// <summary>
///     Credential guard regression tests, blueprint seed: a blueprint whose seed data carries a value for a Hidden
///     (<c>PasswordHash</c>, <c>SecurityStamp</c>, <c>UserTokens</c>) or MethodOnly (<c>UserName</c>, <c>Email</c>)
///     attribute of the real System.Identity user is refused before anything is written, and neither the result nor a log
///     line carries the dummy values. Test blueprints: <c>TestBlueprints/blueprints/v1/CredentialSeedBp</c>,
///     <c>MethodOnlySeedBp</c> and the positive control <c>VisibleIdentitySeedBp</c>.
/// </summary>
[Collection(IdentitySeedBlueprintCollection.Name)]
public class IdentityCredentialGuardBlueprintSeedTests(IdentitySeedBlueprintFixture fixture)
{
    private const int TestTimeoutMs = 180_000;

    private static readonly RtCkId<CkTypeId> UserCkType = new(IdentityCredentialGuardFixture.UserCkTypeId);

    /// <summary>The rtId of the user in <c>NewUserSeedBp</c>.</summary>
    private const string NewSeedUserRtId = "67000099aaaa7777bbbb0001";

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SeedOfANewUserWithVisibleAttributesOnly_IsRefusedWithUserCreateUser_AndWritesNoUser()
    {
        // System.Identity 3.0.0 (AB#8053): CreateUser-1 carries replacesGenericCreate in the model. Before 3.0.0 this seed
        // (visible attributes only) was applied as a positive control; now no seed can create a user.
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await fixture.CreateTenantAsync("seed-visible");
        try
        {
            var outcome = await ApplySeedAsync(tenantId, "VisibleIdentitySeedBp-1.0.0", ct);

            outcome.Should().Contain("[136]").And.Contain("System.Identity/User.CreateUser-1");
            (await CountUsersAsync(tenantId, ct)).Should().Be(0, "nothing is written");
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SeedOfANewUser_IsRefusedWithUserCreateUser_AndWritesNoUser()
    {
        // AB#7777 / AB#8053: a user is created only by the Identity Service (REST or User.CreateUser); the model says so (3.0.0).
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await fixture.CreateTenantAsync("seed-new");
        try
        {
            var outcome = await ApplyNewUserSeedAsync(tenantId, ct);

            outcome.Should().Contain("[136]").And.Contain("System.Identity/User.CreateUser-1")
                .And.Contain("GENERIC_CREATE_REPLACED_BY_METHOD").And.Contain(NewSeedUserRtId);
            (await CountUsersAsync(tenantId, ct)).Should().Be(0, "nothing is written");
            (await fixture.GetBlueprintInstallations().GetByBlueprintNameAsync(tenantId, "NewUserSeedBp", ct))
                .Should().BeNull("the blueprint is not recorded as installed");
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SeedOfAnExistingUserRtId_IsUpserted_PositiveControl()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await fixture.CreateTenantAsync("seed-upsert");
        try
        {
            // The first apply installs System.Identity 3.0 and is refused (new user); then the user exists - created through
            // the repository, as the Identity Service does - and the same seed only updates it.
            (await ApplyNewUserSeedAsync(tenantId, ct)).Should().Contain("[136]");
            var repository = await fixture.GetRuntimeRepositoryProvider().GetRepositoryAsync(tenantId, ct);
            using (var session = await repository!.GetSessionAsync())
            {
                session.StartTransaction();
                var user = new RtEntity(UserCkType, new OctoObjectId(NewSeedUserRtId));
                user.SetAttributeRawValue("FirstName", "Existing");
                await repository.InsertOneRtEntityAsync(session, user);
                await session.CommitTransactionAsync();
            }

            var result = await fixture.GetBlueprintService()
                .ApplyBlueprintAsync(tenantId, new BlueprintId("NewUserSeedBp-1.0.0"), false, ct);

            result.IsSuccess.Should().BeTrue(Messages(result.OperationResult));
            (await CountUsersAsync(tenantId, ct)).Should().Be(1);
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    private Task<string> ApplyNewUserSeedAsync(string tenantId, CancellationToken ct) =>
        ApplySeedAsync(tenantId, "NewUserSeedBp-1.0.0", ct);

    private async Task<string> ApplySeedAsync(string tenantId, string blueprintId, CancellationToken ct)
    {
        try
        {
            var result = await fixture.GetBlueprintService()
                .ApplyBlueprintAsync(tenantId, new BlueprintId(blueprintId), false, ct);
            result.IsSuccess.Should().BeFalse("a blueprint cannot create a user");
            return Messages(result.OperationResult);
        }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException)
        {
            return e.ToString();
        }
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData("CredentialSeedBp-1.0.0", "PasswordHash")]
    [InlineData("MethodOnlySeedBp-1.0.0", "UserName")]
    [InlineData("MethodOnlySeedBp-1.0.0", "TwoFactorEnabled")] // AB#8053: an identity-owned attribute of the former runtime list
    public async Task SeedWithARestrictedUserAttribute_IsRefused_AndWritesNoUser(string blueprintId, string attribute)
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await fixture.CreateTenantAsync("seed-no");
        var logStart = LogCount();
        try
        {
            string outcome;
            try
            {
                var result = await fixture.GetBlueprintService()
                    .ApplyBlueprintAsync(tenantId, new BlueprintId(blueprintId), false, ct);
                result.IsSuccess.Should().BeFalse("the seed data writes an attribute only a CK method may write");
                outcome = Messages(result.OperationResult);
            }
            catch (Exception e) when (e is not Xunit.Sdk.XunitException)
            {
                outcome = e.ToString();
            }

            outcome.Should().Contain(attribute).And.Contain("139");
            foreach (var dummy in IdentityCredentialGuardFixture.DummyValues)
            {
                outcome.Should().NotContain(dummy);
            }

            (await CountUsersAsync(tenantId, ct)).Should().Be(0, "nothing is written");
            LogLinesWithDummyValuesSince(logStart).Should().BeEmpty("no log line may carry a credential");
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    private async Task<int> CountUsersAsync(string tenantId, CancellationToken ct)
    {
        var repository = await fixture.GetRuntimeRepositoryProvider().GetRepositoryAsync(tenantId, ct);
        repository.Should().NotBeNull();
        using var session = await repository!.GetSessionAsync();
        var users = await repository.GetRtEntitiesByTypeAsync(session, UserCkType, RtEntityQueryOptions.Create());
        return users.Items.Count();
    }

    private int LogCount()
    {
        lock (fixture.Logs)
        {
            return fixture.Logs.Count;
        }
    }

    private List<string> LogLinesWithDummyValuesSince(int start)
    {
        lock (fixture.Logs)
        {
            return fixture.Logs.Skip(start)
                .Where(l => IdentityCredentialGuardFixture.DummyValues.Any(d => l.Contains(d, StringComparison.Ordinal)))
                .ToList();
        }
    }

    private static string Messages(OperationResult result) =>
        string.Join("; ", result.Messages.Select(m => $"[{m.MessageNumber}] {m.MessageText}"));
}
