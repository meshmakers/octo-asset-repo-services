using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     AB#7037: the method gateway fixture (real schema, real gateway, MongoDB, dispatcher double) with data policies on
///     <c>AccessTestAccount</c>. Which policies a caller holds is decided by its roles, so one static table serves every
///     variant. The table reaches the real engine (read filter, write guard and the public check API) through the
///     <see cref="IDataPermissionResolver" /> seam, the pattern of <see cref="HiddenRecordGraphQlTestFixture" />.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class MethodDataPermissionGraphQlTestFixture : MethodGatewayGraphQlTestFixture
{
    public const string CkTypeId = "AssetRepositoryIntegrationTest/AccessTestAccount";

    /// <summary>Write on every account.</summary>
    public const string WriteAllRole = "AcctWriteAll";

    /// <summary>Read on every account.</summary>
    public const string ReadAllRole = "AcctReadAll";

    /// <summary>Read on the caller's own accounts only.</summary>
    public const string ReadOwnRole = "AcctReadOwn";

    /// <summary>Write on the caller's own accounts only.</summary>
    public const string WriteOwnRole = "AcctWriteOwn";

    public MethodDataPermissionGraphQlTestFixture()
    {
        var types = new HashSet<string> { CkTypeId };
        var table = new RtDataPolicyTable(
        [
            Rule("acct-write-all", RtDataAction.Write, ownedOnly: false, WriteAllRole),
            Rule("acct-read-all", RtDataAction.Read, ownedOnly: false, ReadAllRole),
            Rule("acct-read-own", RtDataAction.Read, ownedOnly: true, ReadOwnRole),
            Rule("acct-write-own", RtDataAction.Write, ownedOnly: true, WriteOwnRole)
        ]);
        Services.AddSingleton<IDataPermissionResolver>(new FixedDataPermissionResolver(table));

        RtDataPolicyRule Rule(string name, RtDataAction action, bool ownedOnly, string role) =>
            new(name, types, [action], OwnedOnly: ownedOnly, AuditOnly: false, new HashSet<string> { role });
    }

    private sealed class FixedDataPermissionResolver(RtDataPolicyTable table) : IDataPermissionResolver
    {
        public Task<RtDataPolicyTable> GetPolicyTableAsync(IRuntimeRepository runtimeRepository) => Task.FromResult(table);

        public void Invalidate(string tenantId)
        {
        }
    }
}
