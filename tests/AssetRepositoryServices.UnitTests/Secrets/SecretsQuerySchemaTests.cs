using System.Security.Claims;
using FakeItEasy;
using FluentAssertions;
using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Secrets;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Secrets;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AssetRepositoryServices.UnitTests.Secrets;

/// <summary>
///     AB#5544 (handover §7, §12): the GraphQL surface of the secrets overview and its role check.
/// </summary>
public class SecretsQuerySchemaTests
{
    private static IEnumerable<string> FieldNames(IComplexGraphType type)
    {
        // Field names are converted to camelCase when the schema is initialised; compare lower-case.
        return type.Fields.Select(f => f.Name.ToLowerInvariant());
    }

    [Fact]
    public void SecretsQuery_HasInventorySummaryAndUsages()
    {
        var query = new SecretsQuery(NullLogger<SecretsQuery>.Instance);

        query.Name.Should().Be("SecretsQuery");
        FieldNames(query).Should().BeEquivalentTo("inventory", "summary", "usages");

        var inventory = query.Fields.Find("inventory")!;
        inventory.Arguments!.Select(a => a.Name).Should()
            .BeEquivalentTo("first", "after", "ckTypeId", "forms", "needsReEntry", "search");
        inventory.Arguments!.Find("first")!.DefaultValue.Should().Be(50);
        inventory.Type.Should().Be<NonNullGraphType<SecretInventoryConnectionGraphType>>();

        var usages = query.Fields.Find("usages")!;
        usages.Arguments!.Select(a => a.Name).Should().BeEquivalentTo("ckTypeId", "rtId", "attributePath");
        usages.Arguments!.All(a => a.Type!.Name.StartsWith("NonNull")).Should().BeTrue();
    }

    [Fact]
    public void InventoryItem_HasExactlyTheContractFields()
    {
        FieldNames(new SecretInventoryItemGraphType()).Should().BeEquivalentTo(
            "cktypeid", "rtid", "rtwellknownname", "displayname", "attributepath", "attributename", "required",
            "form", "keyid", "setat", "needsreentry", "usedby");
    }

    [Fact]
    public void Connection_Usage_Summary_HaveTheContractFields()
    {
        FieldNames(new SecretInventoryConnectionGraphType()).Should()
            .BeEquivalentTo("totalcount", "pageinfo", "items");
        FieldNames(new SecretUsageGraphType()).Should().BeEquivalentTo(
            "dataflowrtid", "dataflowname", "pipelinertid", "pipelinename", "nodepath", "match");
        FieldNames(new SecretInventorySummaryGraphType()).Should().BeEquivalentTo(
            "total", "notset", "plaintext", "encv1", "encv2", "keymissing", "corrupt", "needsreentry",
            "encv2bykeyid");
        FieldNames(new KeyIdCountGraphType()).Should().BeEquivalentTo("keyid", "count");
    }

    [Fact]
    public void Enums_HaveTheContractValues()
    {
        new SecretStorageFormGraphType().Values.Select(v => v.Name).Should().Equal(
            "NOT_SET", "PLAINTEXT", "ENC_V1", "ENC_V2", "KEY_MISSING", "CORRUPT");
        new SecretUsageMatchGraphType().Values.Select(v => v.Name).Should().Equal("EXACT", "BY_TYPE");
    }

    [Fact]
    public void Resolve_WithoutAdminPanelRole_IsForbidden()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "editor")], "Test"));
        var context = CreateContext(user);

        SecretsQuery.Resolve(context).Should().BeNull();

        context.Errors.Should().ContainSingle().Which.Code.Should().Be("Forbidden");
    }

    [Fact]
    public void Resolve_Unauthenticated_IsForbidden()
    {
        var context = CreateContext(null);

        SecretsQuery.Resolve(context).Should().BeNull();
        context.Errors.Should().ContainSingle().Which.Code.Should().Be("Forbidden");
    }

    [Fact]
    public void Resolve_WithAdminPanelRole_ReturnsTheRequestContext()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, CommonConstants.AdminPanelManagementRole)], "Test"));
        var context = CreateContext(user);

        SecretsQuery.Resolve(context).Should().BeOfType<SecretsRequestContext>();
        context.Errors.Should().BeEmpty();
    }

    private static ResolveFieldContext<object?> CreateContext(ClaimsPrincipal? user)
    {
        var services = new ServiceCollection()
            .AddSingleton(A.Fake<IPipelineDefinitionSource>())
            .AddSingleton<Microsoft.Extensions.Logging.ILogger<SecretUsageScanner>>(NullLogger<SecretUsageScanner>.Instance)
            .AddSingleton<SecretUsageScanner>()
            .BuildServiceProvider();

        return new ResolveFieldContext<object?>
        {
            UserContext = new GraphQlUserContext(user, A.Fake<ITenantContext>()),
            Errors = new ExecutionErrors(),
            RequestServices = services
        };
    }
}
