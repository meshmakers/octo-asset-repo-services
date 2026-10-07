using System.Text.Json;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using MongoDB.Bson;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.CkV2;

/// <summary>
///     CK v2 Phase 0 (AB#5668, contract §4.1 / §8.3): an attribute assigned with <c>access: Hidden</c> never appears in
///     GraphQL output, input, update, generic attribute lists or query columns, cannot be written through the generic
///     mutations or a query row, and cannot be used as a filter / sort oracle. <c>MethodOnly</c> is readable but not
///     generically writable. Test type: <c>AssetRepositoryIntegrationTest/AccessTestAccount</c>.
/// </summary>
[Collection(GraphQlMutatingCollection.Name)]
public class HiddenAttributeTests
{
    private const string CkTypeId = "AssetRepositoryIntegrationTest/AccessTestAccount";
    private const string TypeName = "AssetRepositoryIntegrationTestAccessTestAccount";
    private const string CollectionSuffix = "AssetRepositoryIntegrationTestAccessTestAccount";
    private const string StoredHash = "AQAAAAIAAYagAAAAEStoredHashThatMustNeverLeave";

    private readonly GraphQlTestFixture _fixture;

    public HiddenAttributeTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    [Fact]
    public async Task Introspection_HiddenIsAbsentEverywhere_MethodOnlyOnlyInOutput()
    {
        var output = await FieldNamesAsync(TypeName, "fields");
        var input = await FieldNamesAsync(TypeName + "Input", "inputFields");
        var update = await FieldNamesAsync(TypeName + "InputUpdate", "inputFields");

        output.Should().Contain(["name", "label", "approvalState"]).And.NotContain("passwordHash");
        input.Should().Contain(["name", "label"]).And.NotContain("passwordHash").And.NotContain("approvalState");
        update.Should().NotContain("passwordHash").And.NotContain("approvalState");
    }

    [Fact]
    public async Task CkMeta_ExposesAccessPerAttribute()
    {
        var result = await ExecuteAsync($$"""
            query {
              constructionKit {
                types(rtCkId: "{{CkTypeId}}") {
                  items { attributes(first: 50) { items { attributeName access } } }
                }
              }
            }
            """);

        var access = result.SelectTokens("data.constructionKit.types.items[0].attributes.items[*]")
            .ToDictionary(a => a["attributeName"]!.Value<string>()!, a => a["access"]!.Value<string>());
        access["passwordHash"].Should().Be("Hidden");
        access["approvalState"].Should().Be("MethodOnly");
        access["name"].Should().Be("ReadWrite");
    }

    [Fact]
    public async Task GenericCreate_WithHiddenAttribute_IsRejected()
    {
        var errors = await GenericCreateErrorsAsync("passwordHash", "x");

        errors.Should().ContainSingle().Which.Code.Should().Be("ATTRIBUTE_NOT_WRITABLE");
        errors[0].Message.Should().Contain("PasswordHash").And.Contain("access: Hidden");
    }

    [Fact]
    public async Task GenericCreate_WithMethodOnlyAttribute_IsRejected()
    {
        var errors = await GenericCreateErrorsAsync("approvalState", "Approved");

        errors.Should().ContainSingle().Which.Code.Should().Be("ATTRIBUTE_NOT_WRITABLE");
        errors[0].Message.Should().Contain("access: MethodOnly");
    }

    [Fact]
    public async Task GenericUpdate_OfHiddenAttribute_IsRejected_AndStoredValueIsUnchanged()
    {
        var rtId = await CreateAccountWithStoredHashAsync("update-rejected");

        var result = await _fixture.ExecuteGraphQlAsync("""
            mutation ($entities: [RtEntityUpdate!]!) {
              runtime { runtimeEntities { update(entities: $entities) { rtId } } }
            }
            """, JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    rtId,
                    item = new
                    {
                        ckTypeId = CkTypeId,
                        attributes = new[] { new { attributeName = "passwordHash", value = "x" } }
                    }
                }
            }
        }));

        result.Errors.Should().NotBeNull();
        result.Errors!.Select(e => e.Code).Should().Contain("ATTRIBUTE_NOT_WRITABLE");
        (await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "passwordHash", CollectionSuffix)).AsString
            .Should().Be(StoredHash);
    }

    [Fact]
    public async Task GenericAttributeList_DoesNotContainHiddenValue()
    {
        var rtId = await CreateAccountWithStoredHashAsync("generic-read");

        var json = await ExecuteRawJsonAsync($$"""
            query {
              runtime {
                runtimeEntities(rtIds: ["{{rtId}}"], ckId: "{{CkTypeId}}") {
                  items { attributes(first: 50) { items { attributeName value } } }
                }
              }
            }
            """);

        json.Should().NotContain(StoredHash).And.NotContain("passwordHash");
        json.Should().Contain("approvalState");
    }

    [Fact]
    public async Task AvailableQueryColumns_DoNotOfferHiddenAttribute()
    {
        var json = await ExecuteRawJsonAsync($$"""
            query {
              constructionKit {
                types(rtCkId: "{{CkTypeId}}") {
                  items { availableQueryColumns(first: 100) { items { attributePath } } }
                }
              }
            }
            """);

        json.Should().Contain("\"name\"").And.NotContain("passwordHash");
    }

    [Fact]
    public async Task TransientQuery_WithHiddenColumn_IsRejected()
    {
        await CreateAccountWithStoredHashAsync("transient-column");

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            query {
              runtime {
                transientQuery {
                  simple(ckId: "{{CkTypeId}}", columnPaths: ["name", "passwordHash"]) {
                    items { rows { items { ... on RtSimpleQueryRow { cells { items { attributePath value } } } } } }
                  }
                }
              }
            }
            """);

        AssertRejected(result, "ATTRIBUTE_NOT_QUERYABLE");
    }

    [Theory]
    [InlineData("EQUALS")]
    [InlineData("LIKE")]
    [InlineData("IS_NOT_NULL")]
    public async Task TypedQuery_FilterOnHiddenAttribute_IsRejected(string op)
    {
        var comparison = op == "IS_NOT_NULL" ? "" : ", comparisonValue: \"AQ\"";
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            query {
              runtime {
                assetRepositoryIntegrationTestAccessTestAccount(
                  fieldFilter: [{ attributePath: "passwordHash", operator: {{op}}{{comparison}} }]) {
                  totalCount
                }
              }
            }
            """);

        AssertRejected(result, "ATTRIBUTE_NOT_QUERYABLE");
    }

    [Fact]
    public async Task TypedQuery_SortOnHiddenAttribute_IsRejected()
    {
        var result = await _fixture.ExecuteGraphQlAsync("""
            query {
              runtime {
                assetRepositoryIntegrationTestAccessTestAccount(
                  sortOrder: [{ attributePath: "passwordHash", sortOrder: ASCENDING }]) {
                  totalCount
                }
              }
            }
            """);

        AssertRejected(result, "ATTRIBUTE_NOT_QUERYABLE");
    }

    [Theory]
    [InlineData("passwordHash", "Hidden")]
    [InlineData("approvalState", "MethodOnly")]
    public async Task QueryRowCreate_WithHiddenOrMethodOnlyCell_IsNotWritable(string cell, string access)
    {
        // Review M7: the query-row write path applies the same rule as the generic mutations.
        var queryRtId = await CreatePersistentQueryAsync();

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation {
              runtime { runtimeQuery(rtId: "{{queryRtId}}") {
                create(entities: [{ ckTypeId: "{{CkTypeId}}", cells: [
                  { attributePath: "name", value: "row-{{cell}}" },
                  { attributePath: "{{cell}}", value: "x" }
                ] }]) { ckTypeId }
              } }
            }
            """);

        result.Errors.Should().NotBeNull();
        var error = result.Errors!.Should().ContainSingle(e => e.Code == "ATTRIBUTE_NOT_WRITABLE").Subject;
        error.Message.Should().Contain($"access: {access}");
    }

    [Fact]
    public async Task QueryRowCreate_WithVisibleCells_StillWorks()
    {
        var queryRtId = await CreatePersistentQueryAsync();

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation {
              runtime { runtimeQuery(rtId: "{{queryRtId}}") {
                create(entities: [{ ckTypeId: "{{CkTypeId}}", cells: [
                  { attributePath: "name", value: "row-visible" }, { attributePath: "label", value: "l" }
                ] }]) { ckTypeId }
              } }
            }
            """);

        result.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(result));
    }

    [Fact]
    public async Task EntitySelectorOnHiddenAttribute_IsRejected()
    {
        // Review L10: an entity selector is an equality lookup on its key.
        var result = await _fixture.ExecuteGraphQlAsync("""
            query {
              runtime {
                transientQuery {
                  simple(ckId: "AssetRepositoryIntegrationTest/AccessTestGroup",
                         columnPaths: ["name", "members.AssetRepositoryIntegrationTest/AccessTestAccount[passwordHash=AQ]->name"]) {
                    items { rows { items { ... on RtSimpleQueryRow { cells { items { attributePath value } } } } } }
                  }
                }
              }
            }
            """);

        AssertRejected(result, "ATTRIBUTE_NOT_QUERYABLE");
    }

    private async Task<string> CreatePersistentQueryAsync()
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation {
              runtime { systemSimpleRtQuerys {
                create(entities: [{ name: "access-rows", queryCkTypeId: "{{CkTypeId}}", columns: ["name"] }]) { rtId }
              } }
            }
            """);
        result.Errors.Should().BeNullOrEmpty(_fixture.SerializeGraphQl(result));
        return JObject.Parse(_fixture.SerializeGraphQl(result))
            .SelectToken("data.runtime.systemSimpleRtQuerys.create[0].rtId")!.Value<string>()!;
    }

    private static void AssertRejected(ExecutionResult result, string code)
    {
        result.Errors.Should().NotBeNull();
        result.Errors!.Select(e => e.Code).Should().Contain(code);
        result.Errors!.Select(e => e.Message).Should().NotContain(m => m.Contains(StoredHash));
    }

    private async Task<string> CreateAccountWithStoredHashAsync(string name)
    {
        var result = await _fixture.ExecuteGraphQlAsync("""
            mutation ($entities: [RtEntityInput!]!) {
              runtime { runtimeEntities { create(entities: $entities) { rtId } } }
            }
            """, JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = CkTypeId,
                    attributes = new[] { new { attributeName = "name", value = name } }
                }
            }
        }));
        result.Errors.Should().BeNullOrEmpty();
        var rtId = JObject.Parse(_fixture.SerializeGraphQl(result))
            .SelectToken("data.runtime.runtimeEntities.create[0].rtId")!.Value<string>()!;

        // The identity store writes PasswordHash through the repository; simulate that write here.
        await _fixture.SetRawAttributeValueInMongoDb(rtId, "passwordHash", new BsonString(StoredHash),
            CollectionSuffix);
        return rtId;
    }

    private async Task<List<ExecutionError>> GenericCreateErrorsAsync(string attributeName, string value)
    {
        var result = await _fixture.ExecuteGraphQlAsync("""
            mutation ($entities: [RtEntityInput!]!) {
              runtime { runtimeEntities { create(entities: $entities) { rtId } } }
            }
            """, JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = CkTypeId,
                    attributes = new[]
                    {
                        new { attributeName = "name", value = "generic-" + attributeName },
                        new { attributeName, value }
                    }
                }
            }
        }));

        result.Errors.Should().NotBeNull();
        return result.Errors!.ToList();
    }

    private async Task<List<string>> FieldNamesAsync(string typeName, string fieldsKey)
    {
        var answer = await ExecuteAsync($$"""
            query { __type(name: "{{typeName}}") { {{fieldsKey}} { name } } }
            """);
        var fields = answer.SelectTokens($"data.__type.{fieldsKey}[*].name").Select(t => t.Value<string>()!)
            .ToList();
        fields.Should().NotBeEmpty($"type {typeName} must exist");
        return fields;
    }

    private async Task<JObject> ExecuteAsync(string query)
    {
        var result = await _fixture.ExecuteGraphQlAsync(query);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return JObject.Parse(json);
    }

    private async Task<string> ExecuteRawJsonAsync(string query)
    {
        var result = await _fixture.ExecuteGraphQlAsync(query);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return json;
    }
}
