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
///     The engine's released-document lock behind the real GraphQL mutations. A <c>ReleasedDocumentTestInvoice</c>
///     gets its number from the range <c>ReleasedDocumentTestInvoiceNumber</c>, whose release configuration makes it
///     released at <c>Status = Released</c>. A typed update and a generic delete of a released document answer the
///     stable error <c>RELEASED_DOCUMENT_LOCKED</c> (message 168, the document named in the extensions) and the stored
///     document stays byte for byte the same; the same calls on a draft succeed. The refused write leaves a
///     <c>Denied</c> audit entry, and deleting that entry answers <c>DOCUMENT_AUDIT_ENTRY_APPEND_ONLY</c> (173), mapped
///     on the same path.
/// </summary>
[Collection(MethodDataPermissionCollection.Name)]
public class ReleasedDocumentLockGraphQlTests
{
    private const string InvoiceField = "assetRepositoryIntegrationTestReleasedDocumentTestInvoices";
    private const string InvoiceCollection = "RtEntity_AssetRepositoryIntegrationTestReleasedDocumentTestInvoice";
    private const string Range = "ReleasedDocumentTestInvoiceNumber";
    private const string AuditEntryCkTypeId = "System/DocumentAuditEntry";

    private readonly MethodDataPermissionGraphQlTestFixture _fixture;

    public ReleasedDocumentLockGraphQlTests(MethodDataPermissionGraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    private ClaimsPrincipal Writer
    {
        get
        {
            var claims = new List<Claim>
            {
                new("sub", "released-writer"), new("scope", "octo_api openid"),
                new("tenant_id", _fixture.SystemTenantId),
                new("role", MethodDataPermissionGraphQlTestFixture.WriteAllRole),
                new("role", MethodDataPermissionGraphQlTestFixture.ReadAllRole)
            };
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }
    }

    private IMongoDatabase Database =>
        new MongoClient(_fixture.GetConnectionString()).GetDatabase(_fixture.SystemDatabaseName);

    /// <summary>The range with its release configuration, as a tenant administrator configures it.</summary>
    private async Task StoreRangeAsync()
    {
        var now = DateTime.UtcNow;
        await Database.GetCollection<BsonDocument>("RtEntity_SystemAutoIncrement").ReplaceOneAsync(
            new BsonDocument("rtWellKnownName", Range),
            new BsonDocument
            {
                ["ckTypeId"] = "System/AutoIncrement",
                ["rtWellKnownName"] = Range,
                ["rtCreationDateTime"] = now,
                ["rtChangedDateTime"] = now,
                ["attributes"] = new BsonDocument
                {
                    ["currentValue"] = 0,
                    ["end"] = 9999,
                    ["format"] = "RD-{0:D4}",
                    ["release"] = new BsonDocument
                    {
                        ["ckRecordId"] = "System/NumberRangeRelease",
                        ["attributes"] = new BsonDocument
                        {
                            ["stateAttribute"] = "Status",
                            ["releasedValues"] = new BsonArray { "Released" }
                        }
                    }
                }
            }, new ReplaceOptions { IsUpsert = true }, TestContext.Current.CancellationToken);
    }

    private async Task<string> CreateInvoiceAsync(string status)
    {
        await StoreRangeAsync();
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{InvoiceField}} {
              create(entities: [{ name: "rd-{{Guid.NewGuid():N}}", status: "{{status}}" }]) { rtId number }
            } } }
            """, user: Writer);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        var created = JObject.Parse(json).SelectToken($"data.runtime.{InvoiceField}.create[0]")!;
        if (status == "Released")
        {
            created["number"]!.Value<string>().Should().StartWith("RD-", "a released document gets its number");
        }

        return created["rtId"]!.Value<string>()!;
    }

    private Task<BsonDocument?> RawAsync(string rtId) =>
        Database.GetCollection<BsonDocument>(InvoiceCollection)
            .Find(new BsonDocument("_id", ObjectId.Parse(rtId)))
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken)!;

    private Task<ExecutionResult> UpdateAsync(string rtId) =>
        _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{InvoiceField}} {
              update(entities: [{ rtId: "{{rtId}}", item: { name: "changed" } }]) { rtId }
            } } }
            """, user: Writer);

    private Task<ExecutionResult> DeleteAsync(string rtId, string ckTypeId) =>
        _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { runtimeEntities {
              delete(entities: [{ rtId: "{{rtId}}", ckTypeId: "{{ckTypeId}}" }])
            } } }
            """, user: Writer);

    private JToken SingleError(ExecutionResult result)
    {
        var json = _fixture.SerializeGraphQl(result);
        var errors = (JArray?)JObject.Parse(json)["errors"];
        errors.Should().NotBeNull(json);
        return errors!.Should().ContainSingle(json).Subject;
    }

    private JToken AssertLocked(ExecutionResult result, string rtId, string operation)
    {
        var error = SingleError(result);
        error.SelectToken("extensions.code")!.Value<string>().Should().Be("RELEASED_DOCUMENT_LOCKED");
        error.SelectToken("extensions.messageNumber")!.Value<int>().Should().Be(168);
        error.SelectToken("extensions.rtId")!.Value<string>().Should().Be(rtId);
        error.SelectToken("extensions.ckTypeId")!.Value<string>().Should()
            .Be("AssetRepositoryIntegrationTest/ReleasedDocumentTestInvoice");
        error.SelectToken("extensions.operation")!.Value<string>().Should().Be(operation);
        error.SelectToken("extensions.numberRange")!.Value<string>().Should().Be(Range);
        error.SelectToken("extensions.items")!.Should().ContainSingle();
        error["message"]!.Value<string>().Should().StartWith("RELEASED_DOCUMENT_LOCKED: ");
        return error;
    }

    [Fact]
    public async Task Update_OfAReleasedDocument_AnswersReleasedDocumentLocked_AndChangesNothing()
    {
        var rtId = await CreateInvoiceAsync("Released");
        var before = await RawAsync(rtId);

        var error = AssertLocked(await UpdateAsync(rtId), rtId, "update");

        ((JArray)error.SelectToken("extensions.items[0].changes")!).Values<string>().Should()
            .Contain(c => string.Equals(c, "Name", StringComparison.OrdinalIgnoreCase));
        (await RawAsync(rtId))!.ToJson().Should().Be(before!.ToJson());
    }

    [Fact]
    public async Task Delete_OfAReleasedDocument_AnswersReleasedDocumentLocked_AndKeepsIt()
    {
        var rtId = await CreateInvoiceAsync("Released");
        var before = await RawAsync(rtId);

        AssertLocked(await DeleteAsync(rtId, MethodDataPermissionGraphQlTestFixture.ReleasedCkTypeId), rtId, "delete");

        (await RawAsync(rtId))!.ToJson().Should().Be(before!.ToJson());
    }

    [Fact]
    public async Task UpdateAndDelete_OfADraft_Succeed()
    {
        var rtId = await CreateInvoiceAsync("Draft");

        var update = await UpdateAsync(rtId);
        update.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(update));
        (await RawAsync(rtId))!["attributes"]["name"].AsString.Should().Be("changed");

        var delete = await DeleteAsync(rtId, MethodDataPermissionGraphQlTestFixture.ReleasedCkTypeId);
        delete.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(delete));
        // The default delete archives the entity.
        (await RawAsync(rtId))!["rtState"].ToInt32().Should().Be(1);
    }

    [Fact]
    public async Task Delete_OfAnAuditEntry_AnswersDocumentAuditEntryAppendOnly()
    {
        var rtId = await CreateInvoiceAsync("Released");
        AssertLocked(await UpdateAsync(rtId), rtId, "update");
        var denied = await Database.GetCollection<BsonDocument>("RtEntity_SystemDocumentAuditEntry")
            .Find(new BsonDocument("attributes.documentRtId", rtId))
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        denied.Should().NotBeNull("the refused update leaves a Denied entry");

        var error = SingleError(await DeleteAsync(denied!["_id"].AsObjectId.ToString(), AuditEntryCkTypeId));

        error.SelectToken("extensions.code")!.Value<string>().Should().Be("DOCUMENT_AUDIT_ENTRY_APPEND_ONLY");
        error.SelectToken("extensions.messageNumber")!.Value<int>().Should().Be(173);
        (await Database.GetCollection<BsonDocument>("RtEntity_SystemDocumentAuditEntry")
                .CountDocumentsAsync(new BsonDocument("_id", denied["_id"]),
                    cancellationToken: TestContext.Current.CancellationToken))
            .Should().Be(1);
    }
}
