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
///     <c>runtime.numberRangeGapReport(numberRange, fiscalYear)</c> with two users. The documents are
///     <c>NumberRangeTestInvoice</c>s (number from the range <c>NumberRangeTestInvoiceNumber</c>, format
///     <c>INV-{0:D4}</c>, no fiscal-year periods) behind the read policy of the fixture. The report runs with the
///     caller's read filter: a caller with the read grant sees every number and the documents of a duplicate; a caller
///     without it reads no document, so every number of the range shows as missing and no document is named.
/// </summary>
[Collection(MethodDataPermissionCollection.Name)]
public class NumberRangeGapReportQueryTests
{
    private const string InvoiceField = "assetRepositoryIntegrationTestNumberRangeTestInvoices";
    private const string Range = "NumberRangeTestInvoiceNumber";

    private readonly MethodDataPermissionGraphQlTestFixture _fixture;

    public NumberRangeGapReportQueryTests(MethodDataPermissionGraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    private ClaimsPrincipal Reader => User("gap-reader", MethodDataPermissionGraphQlTestFixture.WriteAllRole,
        MethodDataPermissionGraphQlTestFixture.ReadAllRole);

    private ClaimsPrincipal Blind => User("gap-blind");

    private ClaimsPrincipal User(string sub, params string[] roles)
    {
        var claims = new List<Claim>
            { new("sub", sub), new("scope", "octo_api openid"), new("tenant_id", _fixture.SystemTenantId) };
        claims.AddRange(roles.Select(r => new Claim("role", r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private IMongoDatabase Database =>
        new MongoClient(_fixture.GetConnectionString()).GetDatabase(_fixture.SystemDatabaseName);

    /// <summary>The range, stored as a tenant administrator configures it (no periods, no release configuration).</summary>
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
                ["attributes"] = new BsonDocument { ["currentValue"] = 0, ["end"] = 9999, ["format"] = "INV-{0:D4}" }
            }, new ReplaceOptions { IsUpsert = true }, TestContext.Current.CancellationToken);
    }

    private async Task<JObject> CreateInvoiceAsync()
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{InvoiceField}} { create(entities: [{ name: "inv-{{Guid.NewGuid():N}}" }]) { rtId number } } } }
            """, user: Reader);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return (JObject)JObject.Parse(json).SelectToken($"data.runtime.{InvoiceField}.create[0]")!;
    }

    /// <summary>A second document with a number that is already used, inserted bypassing the allocator.</summary>
    private async Task<string> StoreDuplicateAsync(string number)
    {
        var id = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;
        await Database.GetCollection<BsonDocument>("RtEntity_AssetRepositoryIntegrationTestNumberRangeTestInvoice")
            .InsertOneAsync(new BsonDocument
            {
                ["_id"] = id,
                ["ckTypeId"] = MethodDataPermissionGraphQlTestFixture.NumberedCkTypeId,
                ["rtCreationDateTime"] = now,
                ["rtChangedDateTime"] = now,
                ["attributes"] = new BsonDocument { ["name"] = "copy", ["number"] = number }
            }, cancellationToken: TestContext.Current.CancellationToken);
        return id.ToString();
    }

    private async Task<JObject> ReportAsync(ClaimsPrincipal user)
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            query { runtime { numberRangeGapReport(numberRange: "{{Range}}") {
              numberRange fiscalYear startValue currentValue documentCount irregularCount
              missing { from to fromNumber toNumber }
              duplicates { sequenceValue number documents { rtId ckTypeId } }
            } } }
            """, user: user);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return (JObject)JObject.Parse(json).SelectToken("data.runtime.numberRangeGapReport")!;
    }

    [Fact]
    public async Task OnlyACallerWhoMayReadTheDocuments_GetsTheirNumbers()
    {
        await StoreRangeAsync();
        var invoices = new List<JObject>();
        for (var i = 0; i < 3; i++)
        {
            invoices.Add(await CreateInvoiceAsync());
        }

        invoices.Select(i => i["number"]!.Value<string>()).Should().Equal("INV-0001", "INV-0002", "INV-0003");

        var clean = await ReportAsync(Reader);
        clean["fiscalYear"]!.Type.Should().Be(JTokenType.Null);
        clean["documentCount"]!.Value<long>().Should().Be(3);
        clean["currentValue"]!.Value<long>().Should().Be(3);
        ((JArray)clean["missing"]!).Should().BeEmpty();
        ((JArray)clean["duplicates"]!).Should().BeEmpty();

        // A caller without the read grant reads no document: every number shows as missing, none is named.
        var blind = await ReportAsync(Blind);
        blind["documentCount"]!.Value<long>().Should().Be(0);
        var missing = ((JArray)blind["missing"]!).Single();
        (missing["from"]!.Value<long>(), missing["to"]!.Value<long>()).Should().Be((1L, 3L));
        missing["toNumber"]!.Value<string>().Should().Be("INV-0003");
        ((JArray)blind["duplicates"]!).Should().BeEmpty();

        // A duplicate: the reader gets both documents, the blind caller still none.
        var copy = await StoreDuplicateAsync("INV-0002");
        var withDuplicate = await ReportAsync(Reader);
        var duplicate = ((JArray)withDuplicate["duplicates"]!).Single();
        duplicate["number"]!.Value<string>().Should().Be("INV-0002");
        duplicate["documents"]!.Select(d => d["rtId"]!.Value<string>()).Should()
            .BeEquivalentTo(invoices[1]["rtId"]!.Value<string>(), copy);
        ((JArray)withDuplicate["missing"]!).Should().BeEmpty();

        var blindAgain = await ReportAsync(Blind);
        ((JArray)blindAgain["duplicates"]!).Should().BeEmpty();
        blindAgain["documentCount"]!.Value<long>().Should().Be(0);
    }

    [Fact]
    public async Task AnUnknownRange_AnswersNull()
    {
        var result = await _fixture.ExecuteGraphQlAsync("""
            query { runtime { numberRangeGapReport(numberRange: "NoSuchRange") { documentCount } } }
            """, user: Reader);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        JObject.Parse(json).SelectToken("data.runtime.numberRangeGapReport")!.Type.Should().Be(JTokenType.Null);
    }
}
