using System.Security.Claims;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     AB#7213: a GraphQL fixture whose tenant carries the two policies of the hidden-record repro on
///     <c>Customer</c>. P1 (<see cref="WriterRole" />) grants Write and Delete on every record, P2 (<see cref="ReaderRole" />)
///     grants Read on the caller's own records only. Both are Enforce. Which policies a caller holds is decided by its roles,
///     so one static table serves the variant with Read (P1 + P2) and the variant without any Read grant (P1 only).
///     The table reaches the real engine write guard through the <see cref="IDataPermissionResolver" /> seam.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class HiddenRecordGraphQlTestFixture : GraphQlTestFixture
{
    public const string CkTypeId = "AssetRepositoryIntegrationTest/Customer";
    public const string CollectionSuffix = "AssetRepositoryIntegrationTestCustomer";
    public const string WriterRole = "HiddenRecordWriter";
    public const string ReaderRole = "HiddenRecordOwnedReader";

    public HiddenRecordGraphQlTestFixture()
    {
        var types = new HashSet<string> { CkTypeId };
        var table = new RtDataPolicyTable(
        [
            new RtDataPolicyRule("customers-p1-write-delete-all", types, [RtDataAction.Write, RtDataAction.Delete],
                OwnedOnly: false, AuditOnly: false, new HashSet<string> { WriterRole }),
            new RtDataPolicyRule("customers-p2-read-owned", types, [RtDataAction.Read],
                OwnedOnly: true, AuditOnly: false, new HashSet<string> { ReaderRole })
        ]);
        Services.AddSingleton<IDataPermissionResolver>(new FixedDataPermissionResolver(table));
    }

    /// <summary>User A: holds P1 and P2, creates the records.</summary>
    public static ClaimsPrincipal UserA { get; } = CreateUser("user-a", WriterRole, ReaderRole);

    /// <summary>User B with P1 + P2: may write every record, reads only its own.</summary>
    public static ClaimsPrincipal UserBWithOwnedRead { get; } = CreateUser("user-b", WriterRole, ReaderRole);

    /// <summary>User B with P1 only: Write and Delete, no Read grant at all.</summary>
    public static ClaimsPrincipal UserBWithoutRead { get; } = CreateUser("user-b-noread", WriterRole);

    /// <summary>
    ///     Reads the raw <c>rtState</c> of a stored customer (1 = archived by the default delete strategy; absent or 0 = live).
    ///     The document must still exist: the default GraphQL delete archives, it does not erase.
    /// </summary>
    public async Task<int> ReadRtStateAsync(string rtId)
    {
        var collection = new MongoClient(GetConnectionString()).GetDatabase(SystemDatabaseName)
            .GetCollection<BsonDocument>($"RtEntity_{CollectionSuffix}");
        var document = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(rtId)))
            .FirstOrDefaultAsync();
        if (document == null)
        {
            throw new InvalidOperationException($"Customer '{rtId}' is not stored.");
        }

        return document.TryGetValue("rtState", out var state) ? state.ToInt32() : 0;
    }

    private static ClaimsPrincipal CreateUser(string subject, params string[] roles)
    {
        var claims = new List<Claim> { new("sub", subject), new(ClaimTypes.Name, subject) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "IntegrationTests"));
    }

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
