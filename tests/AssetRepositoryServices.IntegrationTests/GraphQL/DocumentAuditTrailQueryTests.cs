using System.Security.Claims;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.CkV2;
using MongoDB.Bson;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL;

/// <summary>
///     <c>runtime.documentAuditTrail(rtId)</c>: the audit trail of a document in time order, only for callers who may read
///     the document. The document is an <c>AccessTestAccount</c> that a data policy protects: a caller with the read grant
///     gets every entry, oldest first; a caller without it gets exactly the answer of a document that does not exist (an
///     empty list). The entries are stored the way the engine writes them; the engine refuses every API write of one.
/// </summary>
[Collection(MethodDataPermissionCollection.Name)]
public class DocumentAuditTrailQueryTests
{
    private const string AccountField = "assetRepositoryIntegrationTestAccessTestAccounts";
    private const string Missing = "5f9b3b3b3b3b3b3b3b3b3b3b";

    private readonly MethodDataPermissionGraphQlTestFixture _fixture;

    public DocumentAuditTrailQueryTests(MethodDataPermissionGraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    private ClaimsPrincipal Reader => User("audit-reader", MethodDataPermissionGraphQlTestFixture.WriteAllRole,
        MethodDataPermissionGraphQlTestFixture.ReadAllRole);

    private ClaimsPrincipal Blind => User("audit-blind");

    private ClaimsPrincipal User(string sub, params string[] roles)
    {
        var claims = new List<Claim>
            { new("sub", sub), new("scope", "octo_api openid"), new("tenant_id", _fixture.SystemTenantId) };
        claims.AddRange(roles.Select(r => new Claim("role", r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private async Task<string> CreateAccountAsync()
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{AccountField}} { create(entities: [{ name: "doc-{{Guid.NewGuid():N}}" }]) { rtId } } } }
            """, user: Reader);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return JObject.Parse(json).SelectToken($"data.runtime.{AccountField}.create[0].rtId")!.Value<string>()!;
    }

    /// <summary>Stores an entry as the engine writes it (the API refuses to create one).</summary>
    private async Task StoreEntryAsync(string documentRtId, int action, DateTime timestamp, string number)
    {
        var collection = new MongoClient(_fixture.GetConnectionString()).GetDatabase(_fixture.SystemDatabaseName)
            .GetCollection<BsonDocument>("RtEntity_SystemDocumentAuditEntry");
        await collection.InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["ckTypeId"] = "System/DocumentAuditEntry",
            ["rtCreationDateTime"] = timestamp,
            ["rtChangedDateTime"] = timestamp,
            ["attributes"] = new BsonDocument
            {
                ["documentRtId"] = documentRtId,
                ["documentCkTypeId"] = MethodDataPermissionGraphQlTestFixture.CkTypeId,
                ["action"] = action,
                ["timestamp"] = timestamp,
                ["subjectId"] = "audit-reader",
                ["documentNumber"] = number
            }
        });
    }

    private Task<ExecutionResult> TrailAsync(string rtId, ClaimsPrincipal user) =>
        _fixture.ExecuteGraphQlAsync($$"""
            query { runtime { documentAuditTrail(rtId: "{{rtId}}") { documentRtId documentNumber timestamp } } }
            """, user: user);

    private JArray Entries(ExecutionResult result)
    {
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return (JArray)JObject.Parse(json).SelectToken("data.runtime.documentAuditTrail")!;
    }

    [Fact]
    public async Task ACallerWhoMayReadTheDocument_GetsEveryEntryInTimeOrder()
    {
        var document = await CreateAccountAsync();
        var released = new DateTime(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc);
        // stored newest first: the answer is ordered by time, not by insertion
        await StoreEntryAsync(document, 2, released.AddHours(2), "denied");
        await StoreEntryAsync(document, 0, released, "RE-2026-00001");
        await StoreEntryAsync(document, 1, released.AddHours(1), "storno");
        await StoreEntryAsync(Missing, 0, released, "another document");

        var entries = Entries(await TrailAsync(document, Reader));

        entries.Select(e => e["documentNumber"]!.Value<string>())
            .Should().Equal("RE-2026-00001", "storno", "denied");
        entries.Select(e => e["documentRtId"]!.Value<string>()).Should().AllBe(document);
    }

    [Fact]
    public async Task ACallerWhoMayNotReadTheDocument_GetsTheAnswerOfAMissingDocument()
    {
        var document = await CreateAccountAsync();
        await StoreEntryAsync(document, 0, DateTime.UtcNow, "RE-2026-00002");

        var hidden = await TrailAsync(document, Blind);
        var missing = await TrailAsync("5f9b3b3b3b3b3b3b3b3b3b3c", Blind);

        Entries(hidden).Should().BeEmpty();
        _fixture.SerializeGraphQl(hidden).Should().Be(_fixture.SerializeGraphQl(missing));
        Entries(await TrailAsync(document, Reader)).Should().ContainSingle();
    }
}
