using System.Security.Claims;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     AB#6385: a GraphQL fixture whose tenant has a real data policy that opts <c>Customer</c> into the
///     blueprint-lock protection (<c>ProtectBlueprintLocked</c>, Enforce, AB#6384). The policy table is supplied through the
///     engine's <see cref="IDataPermissionResolver" /> seam - the System.Identity CK model is not part of the test tenant -
///     and is evaluated by the real engine write guard behind the real GraphQL mutations.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class BlueprintLockGraphQlTestFixture : GraphQlTestFixture
{
    public const string ProtectedCkTypeId = "AssetRepositoryIntegrationTest/Customer";
    public const string CustomerCollectionSuffix = "AssetRepositoryIntegrationTestCustomer";
    public const string EditorRole = "BlueprintEditor";

    public BlueprintLockGraphQlTestFixture()
    {
        var table = new RtDataPolicyTable(
        [
            new RtDataPolicyRule("customers-protect-blueprint-locked", new HashSet<string> { ProtectedCkTypeId },
                [RtDataAction.Read, RtDataAction.Write, RtDataAction.Delete], OwnedOnly: false, AuditOnly: false,
                new HashSet<string> { EditorRole }, ProtectBlueprintLocked: true)
        ]);
        Services.AddSingleton<IDataPermissionResolver>(new FixedDataPermissionResolver(table));
    }

    /// <summary>A tenant user who holds the grant of the policy (may write, but never locked entities).</summary>
    public static ClaimsPrincipal Editor { get; } = new(new ClaimsIdentity(
        [new Claim("sub", "user-editor"), new Claim(ClaimTypes.Role, EditorRole)], "IntegrationTests"));

    private sealed class FixedDataPermissionResolver(RtDataPolicyTable table) : IDataPermissionResolver
    {
        public Task<RtDataPolicyTable> GetPolicyTableAsync(IRuntimeRepository runtimeRepository)
        {
            return Task.FromResult(table);
        }

        public void Invalidate(string tenantId)
        {
        }
    }
}
