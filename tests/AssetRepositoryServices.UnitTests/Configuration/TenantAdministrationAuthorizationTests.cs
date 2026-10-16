using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.RegularExpressions;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AssetRepositoryServices.UnitTests.Configuration;

/// <summary>
///     AB#4497 — creating, attaching, detaching, clearing, deleting and re-running the setup of a child tenant
///     needs the tenant role <c>TenantManagement</c> on top of the <c>octo_api</c> scope; callers without the role get
///     <c>403</c>. Each action is checked with the policy it actually carries, evaluated by the real authorization
///     service with the production policy registration.
/// </summary>
public class TenantAdministrationAuthorizationTests
{
    public static TheoryData<string> AdministrationActions =>
    [
        nameof(TenantsController.Post),
        nameof(TenantsController.Attach),
        nameof(TenantsController.Detach),
        nameof(TenantsController.Clear),
        nameof(TenantsController.Delete),
        nameof(TenantsController.ReRunSetup)
    ];

    private static ClaimsPrincipal Caller(string? scope, params string[] roles)
    {
        var claims = new List<Claim> { new("tenant_id", "t"), new("sub", "s") };
        if (scope != null)
        {
            claims.Add(new Claim(InfrastructureCommon.ClaimScope, scope));
        }

        claims.AddRange(roles.Select(r => new Claim("role", r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer", "name", "role"));
    }

    private static string PolicyOf(string action)
    {
        var attributes = typeof(TenantsController).GetMethod(action)!.GetCustomAttributes<AuthorizeAttribute>().ToList();
        attributes.Should().ContainSingle();
        return attributes[0].Policy!;
    }

    private static async Task<bool> Allows(string policy, ClaimsPrincipal user)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(o => o.AddAssetRepositoryPolicies());
        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, policy);
        return result.Succeeded;
    }

    [Theory]
    [MemberData(nameof(AdministrationActions))]
    public void Action_CarriesTheTenantAdministrationPolicy(string action)
    {
        PolicyOf(action).Should().Be(AssetRepositoryServiceConstants.TenantAdministrationPolicy);
    }

    [Theory]
    [MemberData(nameof(AdministrationActions))]
    public async Task CallerWithScopeButWithoutRole_IsRefused(string action)
    {
        (await Allows(PolicyOf(action), Caller(CommonConstants.OctoApiFullAccess))).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(AdministrationActions))]
    public async Task OtherAdministratorRoles_AreNotEnough(string action)
    {
        var user = Caller(CommonConstants.OctoApiFullAccess, CommonConstants.UserManagementRole,
            CommonConstants.AdminPanelManagementRole);

        (await Allows(PolicyOf(action), user)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(AdministrationActions))]
    public async Task CallerWithScopeAndTenantManagement_IsAllowed(string action)
    {
        var user = Caller(CommonConstants.OctoApiFullAccess, CommonConstants.TenantManagementRole);

        (await Allows(PolicyOf(action), user)).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(AdministrationActions))]
    public async Task RoleWithoutFullAccessScope_IsRefused(string action)
    {
        var user = Caller(CommonConstants.OctoApiReadOnly, CommonConstants.TenantManagementRole);

        (await Allows(PolicyOf(action), user)).Should().BeFalse();
    }

    [Fact]
    public async Task RoleAsInboundMappedRoleClaim_IsRecognised()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess),
                new Claim(ClaimTypes.Role, CommonConstants.TenantManagementRole)
            ], "Bearer", "name", "role"));

        (await Allows(AssetRepositoryServiceConstants.TenantAdministrationPolicy, user)).Should().BeTrue();
    }

    /// <summary>Not part of AB#4497: these keep their previous policy (listed as an open question).</summary>
    [Theory]
    [InlineData(nameof(TenantsController.ClearCache))]
    [InlineData(nameof(TenantsController.RecomputeDisplayNames))]
    public void OtherWriteActions_KeepTheScopeOnlyPolicy(string action)
    {
        PolicyOf(action).Should().Be(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy);
    }

    /// <summary>
    ///     The host registers the policies through the very method the tests evaluate, and Swagger knows the new
    ///     policy's scope.
    /// </summary>
    [Fact]
    public async Task HostWiring_RegistersTheProductionPolicies()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "src", "AssetRepositoryServices",
            "Configuration", "DependencyInjection", "RuntimeEngineBuilderExtensions.cs"), TestContext.Current.CancellationToken);
        var code = Regex.Replace(source, @"//.*?$", string.Empty, RegexOptions.Multiline);

        code.Should().Contain("options.AddAssetRepositoryPolicies()");
        code.Should().Contain("AssetRepositoryServiceConstants.TenantAdministrationPolicy");
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
    }
}
