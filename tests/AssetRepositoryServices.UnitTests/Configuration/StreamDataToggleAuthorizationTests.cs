using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AssetRepositoryServices.UnitTests.Configuration;

/// <summary>
///     AB#7319 — enabling and disabling Stream Data needs the tenant role <c>TenantManagement</c> on top of the
///     <c>octo_api</c> scope; the other stream data actions keep their scope-only policies.
/// </summary>
public class StreamDataToggleAuthorizationTests
{
    public static TheoryData<string> ToggleActions => [nameof(StreamDataController.Enable), nameof(StreamDataController.Disable)];

    private static string PolicyOf(string action)
    {
        var attributes = typeof(StreamDataController).GetMethod(action)!.GetCustomAttributes<AuthorizeAttribute>().ToList();
        attributes.Should().ContainSingle();
        return attributes[0].Policy!;
    }

    private static ClaimsPrincipal Caller(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", "t"), new("sub", "s"),
            new(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess)
        };
        claims.AddRange(roles.Select(r => new Claim("role", r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer", "name", "role"));
    }

    private static async Task<AuthorizationResult> Authorize(string policy, ClaimsPrincipal user)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(o => o.AddAssetRepositoryPolicies());
        services.AddOctoTenantRoleAuthorization();
        await using var provider = services.BuildServiceProvider();
        return await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, policy);
    }

    [Theory]
    [MemberData(nameof(ToggleActions))]
    public void Toggle_CarriesTheTenantAdministrationPolicy(string action)
    {
        PolicyOf(action).Should().Be(AssetRepositoryServiceConstants.TenantAdministrationPolicy);
    }

    [Theory]
    [MemberData(nameof(ToggleActions))]
    public async Task WithoutTheRole_IsRefusedWithAReason(string action)
    {
        var result = await Authorize(PolicyOf(action), Caller());

        result.Succeeded.Should().BeFalse();
        result.Failure!.FailureReasons.Should().ContainSingle(r => r.Message.Contains(CommonConstants.TenantManagementRole));
    }

    [Theory]
    [MemberData(nameof(ToggleActions))]
    public async Task WithTenantManagement_IsAllowed(string action)
    {
        (await Authorize(PolicyOf(action), Caller(CommonConstants.TenantManagementRole))).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void OtherStreamDataActions_KeepTheirScopeOnlyPolicy()
    {
        PolicyOf(nameof(StreamDataController.DisableArchive))
            .Should().Be(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy);
    }
}
