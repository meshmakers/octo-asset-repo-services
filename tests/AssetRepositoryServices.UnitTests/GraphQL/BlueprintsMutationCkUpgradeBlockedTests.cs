using System.Security.Claims;
using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     AB#6935: the CK upgrade guard refusal (AB#6871) of <c>blueprints.install</c> and <c>blueprints.applyUpdate</c>
///     reaches GraphQL clients as ONE error with the structured blockers in <c>extensions</c> (without GraphQL.NET
///     <c>ExposeData</c>), no second "cannot return null" error, a message without the REST-only force option, and the
///     other results unchanged. Runs the real <see cref="BlueprintsMutation" /> and error serialization with a faked
///     <see cref="IBlueprintService" />.
/// </summary>
public class BlueprintsMutationCkUpgradeBlockedTests
{
    private const string TenantId = "sbeg";
    private const string Target = "Shop-2.0.0";

    private readonly IBlueprintService _blueprintService = A.Fake<IBlueprintService>();

    private static BlueprintCkUpgradeBlocker Registration() => new()
    {
        Dependent = "EnergyCommunity.Registration-2.8.0",
        Update = "Basic-2.5.0",
        Kind = "ExactPin",
        Subject = "Basic-2.4.0",
        Code = "CK_UPGRADE_EXACT_PIN",
        Message = "EnergyCommunity.Registration 2.8.0 would break: exact pin Basic-2.4.0: Basic-2.5.0 replaces it",
        DependentIsInUpdateSet = false,
        SuggestedDependentUpdate = "EnergyCommunity.Registration-2.10.0",
        ResolveHint = "update EnergyCommunity.Registration to 2.10.0 first"
    };

    private void GivenInstallIsBlocked()
    {
        // The engine's own text names the REST override; the GraphQL error must not repeat it.
        var op = new OperationResult();
        op.AddMessage(new OperationMessage(MessageLevel.Error, null, 55,
            "would break; repeat the request with ForceBreakingUpdate to proceed anyway"));
        A.CallTo(() => _blueprintService.ApplyBlueprintAsync(TenantId, A<BlueprintId>._, A<BlueprintApplyOptions>._,
                A<CancellationToken>._))
            .Returns(BlueprintApplicationResult.CkUpgradeBlocked(TenantId, new BlueprintId(Target), ["Basic-2.5.0"],
                [Registration()], op));
    }

    private void GivenUpdateIsBlocked() =>
        A.CallTo(() => _blueprintService.ApplyUpdateAsync(TenantId, A<BlueprintId>._, A<BlueprintUpdateMode>._,
                A<BlueprintUpdateOptions?>._, A<CancellationToken>._))
            .Returns(new BlueprintUpdateResult
            {
                Success = false,
                IsCkUpgradeBlocked = true,
                CkUpdateSet = ["Basic-2.5.0"],
                CkUpgradeBlockers = [Registration()],
                Errors = ["would break; repeat the request with ForceBreakingUpdate to proceed anyway"]
            });

    private const string InstallMutation =
        $$"""mutation { blueprints { install(blueprintId: "{{Target}}") { success blueprintId } } }""";

    private const string ApplyUpdateMutation =
        $$"""mutation { blueprints { applyUpdate(input: { targetVersion: "{{Target}}" }) { success blueprintId } } }""";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Refusal_IsOneErrorWithTheBlockersInExtensions_AndNoNullCascade(bool install)
    {
        if (install) GivenInstallIsBlocked(); else GivenUpdateIsBlocked();

        var root = await ExecuteAsync(install ? InstallMutation : ApplyUpdateMutation);

        // One error, no "Cannot return null for a non-null type" second error.
        var errors = root.GetProperty("errors");
        errors.GetArrayLength().Should().Be(1);
        var error = errors[0];
        var extensions = error.GetProperty("extensions");
        extensions.GetProperty("code").GetString().Should().Be("CK_UPGRADE_BLOCKED");
        extensions.GetProperty("blueprintId").GetString().Should().Be(Target);
        extensions.GetProperty("updateSet").EnumerateArray().Select(e => e.GetString()).Should().Equal("Basic-2.5.0");

        var blocker = extensions.GetProperty("blockers").EnumerateArray().Should().ContainSingle().Subject;
        blocker.GetProperty("dependent").GetString().Should().Be("EnergyCommunity.Registration-2.8.0");
        blocker.GetProperty("update").GetString().Should().Be("Basic-2.5.0");
        blocker.GetProperty("kind").GetString().Should().Be("ExactPin");
        blocker.GetProperty("subject").GetString().Should().Be("Basic-2.4.0");
        blocker.GetProperty("code").GetString().Should().Be("CK_UPGRADE_EXACT_PIN");
        blocker.GetProperty("message").GetString().Should().Contain("would break");
        blocker.GetProperty("dependentIsInUpdateSet").GetBoolean().Should().BeFalse();
        blocker.GetProperty("suggestedDependentUpdate").GetString().Should().Be("EnergyCommunity.Registration-2.10.0");
        blocker.GetProperty("resolveHint").GetString().Should().Contain("2.10.0");

        // No force option named for the GraphQL surface; nothing from ExecutionError.Data / exceptions leaks.
        var raw = error.GetRawText();
        raw.Should().NotContain("ForceBreakingUpdate").And.NotContain("proceed anyway");
        error.GetProperty("message").GetString().Should().Contain("Nothing was changed").And.Contain(Target);
        extensions.TryGetProperty("data", out _).Should().BeFalse();
        extensions.TryGetProperty("details", out _).Should().BeFalse();

        // Null is confined to the nullable mutation field: the response keeps its parents.
        var blueprints = root.GetProperty("data").GetProperty("blueprints");
        blueprints.GetProperty(install ? "install" : "applyUpdate").ValueKind.Should().Be(JsonValueKind.Null);
        error.GetProperty("path").EnumerateArray().Select(p => p.GetString())
            .Should().Equal("blueprints", install ? "install" : "applyUpdate");
    }

    [Fact]
    public async Task Install_NotBlocked_ReturnsTheResultUnchanged()
    {
        A.CallTo(() => _blueprintService.ApplyBlueprintAsync(TenantId, A<BlueprintId>._, A<BlueprintApplyOptions>._,
                A<CancellationToken>._))
            .Returns(BlueprintApplicationResult.Success(TenantId, new BlueprintId(Target), [], [], 0,
                new OperationResult()));

        var root = await ExecuteAsync(InstallMutation);

        root.TryGetProperty("errors", out _).Should().BeFalse();
        var install = root.GetProperty("data").GetProperty("blueprints").GetProperty("install");
        install.GetProperty("success").GetBoolean().Should().BeTrue();
        install.GetProperty("blueprintId").GetString().Should().Be(Target);
    }

    [Fact]
    public async Task ApplyUpdate_NotBlocked_ReturnsTheResultUnchanged()
    {
        A.CallTo(() => _blueprintService.ApplyUpdateAsync(TenantId, A<BlueprintId>._, A<BlueprintUpdateMode>._,
                A<BlueprintUpdateOptions?>._, A<CancellationToken>._))
            .Returns(new BlueprintUpdateResult { Success = true });

        var root = await ExecuteAsync(ApplyUpdateMutation);

        root.TryGetProperty("errors", out _).Should().BeFalse();
        root.GetProperty("data").GetProperty("blueprints").GetProperty("applyUpdate").GetProperty("success")
            .GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task OtherFailures_StayOperationFailed_AsOneError()
    {
        A.CallTo(() => _blueprintService.ApplyUpdateAsync(TenantId, A<BlueprintId>._, A<BlueprintUpdateMode>._,
                A<BlueprintUpdateOptions?>._, A<CancellationToken>._))
            .Returns(new BlueprintUpdateResult { Success = false, Errors = ["conflict"] });

        var root = await ExecuteAsync(ApplyUpdateMutation);

        var error = root.GetProperty("errors").EnumerateArray().Should().ContainSingle().Subject;
        error.GetProperty("extensions").GetProperty("code").GetString().Should().Be("OPERATION_FAILED");
        error.GetProperty("message").GetString().Should().Be("conflict");
    }

    [Fact]
    public async Task WithoutTheAdminRole_IsForbidden_AsOneError()
    {
        var root = await ExecuteAsync(InstallMutation, withAdminRole: false);

        var error = root.GetProperty("errors").EnumerateArray().Should().ContainSingle().Subject;
        error.GetProperty("extensions").GetProperty("code").GetString().Should().Be(Statics.GraphQlForbidden);
        A.CallTo(_blueprintService).MustNotHaveHappened();
    }

    [Fact]
    public void MutationFields_AreNullable_SoAnErrorDoesNotCascadeToTheParents()
    {
        var mutation = new BlueprintsMutation(NullLogger<BlueprintsMutation>.Instance);

        foreach (var name in new[] { "install", "applyUpdate", "uninstall" })
        {
            typeof(NonNullGraphType).IsAssignableFrom(mutation.Fields.Find(name)!.Type).Should().BeFalse(name);
        }
    }

    private async Task<JsonElement> ExecuteAsync(string query, bool withAdminRole = true)
    {
        var environment = A.Fake<IHostEnvironment>();
        A.CallTo(() => environment.EnvironmentName).Returns("Production");

        var services = new ServiceCollection();
        services.AddSingleton(environment);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(_blueprintService);
        // The blueprint graph types are resolved by type; register them like the application does.
        foreach (var type in typeof(BlueprintApplyResultDtoType).Assembly.GetTypes()
                     .Where(t => t is { IsAbstract: false, IsClass: true } && t.Namespace == typeof(BlueprintApplyResultDtoType).Namespace
                                 && typeof(IGraphType).IsAssignableFrom(t)))
        {
            services.AddTransient(type);
        }

        services.AddGraphQL(b => b
            .AddSchema<TestSchema>()
            .AddSystemTextJson()
            .AddErrorInfoProvider(GraphQlErrorExposure.Configure));

        await using var provider = services.BuildServiceProvider();
        var tenant = A.Fake<ITenantContext>();
        A.CallTo(() => tenant.TenantId).Returns(TenantId);
        var identity = withAdminRole
            ? new ClaimsIdentity([new Claim(ClaimTypes.Role, CommonConstants.AdminPanelManagementRole)], "Bearer",
                ClaimTypes.Name, ClaimTypes.Role)
            : new ClaimsIdentity("Bearer");

        var executer = provider.GetRequiredService<IDocumentExecuter<TestSchema>>();
        var result = await executer.ExecuteAsync(o =>
        {
            o.Query = query;
            o.RequestServices = provider;
            o.UserContext = new GraphQlUserContext(new ClaimsPrincipal(identity), tenant);
            o.CancellationToken = TestContext.Current.CancellationToken;
        });

        var json = provider.GetRequiredService<IGraphQLTextSerializer>().Serialize(result);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    // ReSharper disable once ClassNeverInstantiated.Local
    private sealed class TestSchema : Schema
    {
        public TestSchema(IServiceProvider serviceProvider) : base(serviceProvider)
        {
            var query = new ObjectGraphType { Name = "Query" };
            query.Field<StringGraphType>("ping").Resolve(_ => "pong");
            Query = query;

            var mutation = new ObjectGraphType { Name = "Mutation" };
            mutation.Field("Blueprints", new BlueprintsMutation(NullLogger<BlueprintsMutation>.Instance))
                .Resolve(_ => new object());
            Mutation = mutation;
        }
    }
}
