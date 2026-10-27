using System.Security.Claims;
using GraphQL;
using GraphQL.Execution;
using GraphQL.Validation;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     AB#6932: GraphQL host for <c>runtime.byInterface</c> with seeded entities of the three implementers of
///     <c>Ranked-1</c> (A and B in their own collections, BChild derived from B in the collection of B) and one
///     non-implementer, plus a data policy that protects type A (read and write need the role <see cref="RoleA" />).
///     The policy reaches the real engine through the <see cref="IDataPermissionResolver" /> seam, the pattern of
///     <see cref="MethodDataPermissionGraphQlTestFixture" />.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class ByInterfaceGraphQlTestFixture : GraphQlTestFixture
{
    public const string ModelPrefix = "AssetRepositoryIntegrationTest";
    public const string CkTypeA = "AssetRepositoryIntegrationTest/InterfaceQueryA";
    public const string RoleA = "RankedA";

    public ByInterfaceGraphQlTestFixture()
    {
        var table = new RtDataPolicyTable(
        [
            new RtDataPolicyRule("ranked-a", new HashSet<string> { CkTypeA }, [RtDataAction.Read, RtDataAction.Write],
                OwnedOnly: false, AuditOnly: false, new HashSet<string> { RoleA })
        ]);
        Services.AddSingleton<IDataPermissionResolver>(new FixedDataPermissionResolver(table));
        // Registered after OctoSessionListener (ctor of the base): runs once the session exists.
        Services.AddSingleton<QueryTransactionListener>();
        Services.AddGraphQL(b => b.AddDocumentListener<QueryTransactionListener>());
    }

    /// <summary>
    ///     While true, every executed query runs in a session with an active transaction (as the pipeline nodes and
    ///     mutations do), then the transaction is aborted. Read-only queries of the real pipeline have none.
    /// </summary>
    public bool RunQueriesInTransaction
    {
        get => GetService<QueryTransactionListener>().Enabled;
        set => GetService<QueryTransactionListener>().Enabled = value;
    }

    /// <summary>A caller that holds the role of the protected type A.</summary>
    public static ClaimsPrincipal PrivilegedUser { get; } = User("privileged", RoleA);

    /// <summary>A caller that holds no role: type A must never appear for it.</summary>
    public static ClaimsPrincipal RestrictedUser { get; } = User("restricted");

    /// <summary>The seeded entities as (type, name, rank) in the expected global order of an ascending rank sort.</summary>
    public static IReadOnlyList<(string Type, string Name, int Rank)> Seed { get; } =
    [
        ("InterfaceQueryA", "a-1", 10), ("InterfaceQueryB", "b-1", 20), ("InterfaceQueryBChild", "c-1", 25),
        ("InterfaceQueryA", "a-2", 30), ("InterfaceQueryB", "b-2", 40), ("InterfaceQueryBChild", "c-2", 45),
        ("InterfaceQueryA", "a-3", 50), ("InterfaceQueryB", "b-3", 60), ("InterfaceQueryA", "a-4", 70)
    ];

    private static ClaimsPrincipal User(string sub, params string[] roles)
    {
        var claims = new List<Claim> { new("sub", sub), new("scope", "octo_api openid") };
        claims.AddRange(roles.Select(r => new Claim("role", r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        foreach (var (type, name, rank) in Seed)
        {
            var extra = type == "InterfaceQueryBChild" ? $", extraChild: \"extra-{name}\"" : string.Empty;
            var result = await ExecuteGraphQlAsync($$"""
                mutation { runtime { {{ModelPrefix.ToLowerInvariant()[0]}}{{ModelPrefix[1..]}}{{type}}s {
                  create(entities: [{ name: "{{name}}", rank: {{rank}}{{extra}} }]) { rtId } } } }
                """, user: PrivilegedUser);
            if (result.Errors is { Count: > 0 })
            {
                throw new InvalidOperationException($"Seeding {name} failed: {SerializeGraphQl(result)}");
            }
        }

        // The non-implementer must never show up in an interface query.
        var other = await ExecuteGraphQlAsync($$"""
            mutation { runtime { assetRepositoryIntegrationTestInterfaceQueryOthers {
              create(entities: [{ name: "other-1" }]) { rtId } } } }
            """, user: PrivilegedUser);
        if (other.Errors is { Count: > 0 })
        {
            throw new InvalidOperationException($"Seeding the non-implementer failed: {SerializeGraphQl(other)}");
        }
    }

    private sealed class QueryTransactionListener : IDocumentExecutionListener
    {
        public bool Enabled { get; set; }

        public Task AfterValidationAsync(IExecutionContext context, IValidationResult validationResult) =>
            Task.CompletedTask;

        public Task BeforeExecutionAsync(IExecutionContext context)
        {
            if (Enabled)
            {
                context.RequestServices!.GetRequiredService<IOctoSessionAccessor>().Session.StartTransaction();
            }

            return Task.CompletedTask;
        }

        public async Task AfterExecutionAsync(IExecutionContext context)
        {
            if (Enabled)
            {
                await context.RequestServices!.GetRequiredService<IOctoSessionAccessor>().AbortAsync();
            }
        }
    }

    private sealed class FixedDataPermissionResolver(RtDataPolicyTable table) : IDataPermissionResolver
    {
        public Task<RtDataPolicyTable> GetPolicyTableAsync(IRuntimeRepository runtimeRepository) => Task.FromResult(table);

        public void Invalidate(string tenantId)
        {
        }
    }
}
