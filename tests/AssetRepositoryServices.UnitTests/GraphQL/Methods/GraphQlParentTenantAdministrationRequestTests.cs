using System.Text;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL.Methods;

/// <summary>
///     AB#8986: the per-request predicate of the HTTP tenant gate on the GraphQL endpoint. Only a request made exclusively of
///     calls of the closed method list or <c>runtime.operations</c> reads is an administration request; everything else,
///     including a mixed, batched or fragment-based request, is not.
/// </summary>
public class GraphQlParentTenantAdministrationRequestTests
{
    private const string Container = "systemBotTenantRepositorys";

    private const string DumpMutation =
        "mutation Dump($input: SystemBotTenantRepositoryDumpMethodInput) { runtime { systemBotTenantRepositorys { dump(input: $input) { invocationId operation { operationId acceptedAtUtc } } } } }";

    private const string OperationsQuery =
        "query Ops($id: ID) { runtime { operations(operationId: $id) { totalCount items { operationId state progress { percent } result error { code } } } } }";

    [Fact]
    public void MutationFields_AreTheGeneratedNamesOfTheClosedList()
    {
        var fields = GraphQlParentTenantAdministrationRequest.MutationFields;

        Assert.Equal([Container], fields.Keys);
        Assert.Equal(["dump", "exportArchiveData", "importArchiveData", "restore"],
            fields[Container].Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(DumpMutation)]
    [InlineData(OperationsQuery)]
    [InlineData("mutation { runtime { systemBotTenantRepositorys { restore(input: { tusFileId: \"x\" }) { invocationId } } } }")]
    [InlineData("mutation { a: runtime { b: systemBotTenantRepositorys { c: exportArchiveData { invocationId } } } }")]
    [InlineData("mutation { runtime { systemBotTenantRepositorys { importArchiveData { invocationId } } } }")]
    [InlineData("query { runtime { operations { items { ... on CkMethodOperation { operationId } } } } }")]
    [InlineData(DumpMutation + " " + OperationsQuery)]
    public void AdministrationDocuments_AreRecognised(string query) =>
        Assert.True(GraphQlParentTenantAdministrationRequest.IsAdministrationDocument(query));

    [Theory]
    // data reads and writes of the child
    [InlineData("query { runtime { runtimeEntities { items { rtId } } } }")]
    [InlineData("query { runtime { systemBotTenantRepository { items { rtId } } } }")]
    [InlineData("mutation { runtime { systemBotTenantRepositorys { create(entities: []) { rtId } } } }")]
    // an alias that names an allowed field over a forbidden one
    [InlineData("mutation { runtime { systemBotTenantRepositorys { dump: create(entities: []) { rtId } } } }")]
    [InlineData("query { runtime { operations: runtimeEntities { totalCount } } }")]
    // mixed requests are refused as a whole
    [InlineData("mutation { runtime { systemBotTenantRepositorys { dump { invocationId } create(entities: []) { rtId } } } }")]
    [InlineData("query { runtime { operations { totalCount } runtimeEntities { totalCount } } }")]
    [InlineData("query { runtime { operations { totalCount } } constructionKit { types { totalCount } } }")]
    [InlineData(OperationsQuery + " query Other { runtime { runtimeEntities { totalCount } } }")]
    // another method of the same type, or the right name under another container
    [InlineData("mutation { runtime { systemBotTenantRepositorys { dump2 { invocationId } } } }")]
    [InlineData("mutation { runtime { systemIdentityUsers { dump { invocationId } } } }")]
    // operations is a query field only, methods are mutations only
    [InlineData("query { runtime { systemBotTenantRepositorys { dump { invocationId } } } }")]
    [InlineData("mutation { runtime { operations { totalCount } } }")]
    // fragments and introspection at the classified levels
    [InlineData("query { runtime { ... on RuntimeModelQuery { runtimeEntities { totalCount } } } }")]
    [InlineData("query { ...F } fragment F on Query { runtime { operations { totalCount } } }")]
    [InlineData("query { runtime { operations { ...G } } } fragment G on CkMethodOperationConnection { totalCount }")]
    [InlineData("query { __schema { types { name } } }")]
    [InlineData("query { __typename }")]
    [InlineData("subscription { runtime { operations { totalCount } } }")]
    [InlineData("")]
    [InlineData("not graphql {")]
    public void EverythingElse_IsNotAnAdministrationRequest(string query) =>
        Assert.False(GraphQlParentTenantAdministrationRequest.IsAdministrationDocument(query));

    private static async Task<(bool Result, string BodyAfter)> ClassifyAsync(string body, string method = "POST",
        string contentType = "application/json", string? queryString = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.ContentType = contentType;
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        if (queryString != null)
        {
            context.Request.QueryString = new QueryString(queryString);
        }

        var result = await new GraphQlParentTenantAdministrationRequest().IsParentTenantAdministrationRequestAsync(context);
        context.Request.Body.Position = 0;
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        return (result, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    private static string Json(string query, string extra = "") =>
        "{\"query\":" + System.Text.Json.JsonSerializer.Serialize(query) + extra + "}";

    [Fact]
    public async Task APostedAdministrationRequest_IsRecognised_AndTheBodyStaysReadable()
    {
        var body = Json(DumpMutation, ",\"variables\":{\"input\":{}},\"operationName\":\"Dump\"");

        var (result, after) = await ClassifyAsync(body);

        Assert.True(result);
        Assert.Equal(body, after);
    }

    [Fact]
    public async Task ABatch_IsRefused_EvenIfEveryElementIsAllowed()
    {
        var (result, _) = await ClassifyAsync("[" + Json(DumpMutation) + "," + Json(OperationsQuery) + "]");

        Assert.False(result);
    }

    [Theory]
    [InlineData("GET", "application/json", null, "")]
    [InlineData("POST", "multipart/form-data; boundary=x", null, "")]
    [InlineData("POST", "application/x-www-form-urlencoded", null, "")]
    [InlineData("POST", "application/json", "?query=%7B__typename%7D", "")]
    [InlineData("POST", "application/json", null, ",\"documentId\":\"abc\"")]
    [InlineData("POST", "application/json", null, ",\"extensions\":{\"persistedQuery\":{\"version\":1}}")]
    public async Task OtherShapes_AreRefused(string method, string contentType, string? queryString, string extra)
    {
        var (result, _) = await ClassifyAsync(Json(DumpMutation, extra), method, contentType, queryString);

        Assert.False(result);
    }

    [Theory]
    // the same member twice, in both orders, or in another spelling: this check and the server could read different values
    [InlineData("{\"query\":ADMIN,\"query\":EVIL}")]
    [InlineData("{\"query\":EVIL,\"query\":ADMIN}")]
    [InlineData("{\"Query\":ADMIN}")]
    [InlineData("{\"query\":ADMIN,\"Query\":EVIL}")]
    public async Task DuplicateOrCaseVariantMembers_AreRefused(string template)
    {
        var body = template
            .Replace("ADMIN", System.Text.Json.JsonSerializer.Serialize(DumpMutation))
            .Replace("EVIL", System.Text.Json.JsonSerializer.Serialize("query { runtime { runtimeEntities { totalCount } } }"));

        var (result, _) = await ClassifyAsync(body);

        Assert.False(result);
    }

    [Theory]
    [InlineData("application/graphql")]
    [InlineData("application/graphql-response+json")]
    [InlineData("text/plain")]
    public async Task OtherBodyTypes_AreRefused(string contentType)
    {
        var (result, _) = await ClassifyAsync(Json(DumpMutation), contentType: contentType);

        Assert.False(result);
    }

    [Fact]
    public async Task ABodyThatIsNotUtf8Json_IsRefused()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json; charset=utf-16";
        context.Request.Body = new MemoryStream(Encoding.Unicode.GetBytes(Json(DumpMutation)));

        Assert.False(await new GraphQlParentTenantAdministrationRequest().IsParentTenantAdministrationRequestAsync(context));
    }

    [Fact]
    public async Task AnEmptyExtensionsObject_IsAccepted()
    {
        var (result, _) = await ClassifyAsync(Json(OperationsQuery, ",\"extensions\":{}"));

        Assert.True(result);
    }

    [Fact]
    public async Task ABodyAboveTheLimit_IsRefused()
    {
        var padding = new string(' ', GraphQlParentTenantAdministrationRequest.MaxBodyBytes);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Json(OperationsQuery + padding)));

        Assert.False(await new GraphQlParentTenantAdministrationRequest().IsParentTenantAdministrationRequestAsync(context));
    }
}
