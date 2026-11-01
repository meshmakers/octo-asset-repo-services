using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
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

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SeedWithVisibleUserAttributes_IsApplied_PositiveControl()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await fixture.CreateTenantAsync("seed-ok");
        try
        {
            var result = await fixture.GetBlueprintService()
                .ApplyBlueprintAsync(tenantId, new BlueprintId("VisibleIdentitySeedBp-1.0.0"), false, ct);

            result.IsSuccess.Should().BeTrue(Messages(result.OperationResult));
            (await CountUsersAsync(tenantId, ct)).Should().Be(1);
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData("CredentialSeedBp-1.0.0", "PasswordHash")]
    [InlineData("MethodOnlySeedBp-1.0.0", "UserName")]
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
