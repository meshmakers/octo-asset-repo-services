using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using MongoDB.Bson;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using Xunit;
using Helpers = Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Helpers;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Security;

/// <summary>
///     AB#8705 end to end: the asset repository derives the principal kind from the caller's token and the engine enforces
///     the <c>writeAccess</c> block of AB#8574 with it. <c>InteractiveOnlyNote</c> (test CK model) admits only
///     <c>Interactive</c>. User tokens (interactive login, device code, cross-tenant token exchange) write; client-credentials,
///     delegated (on-behalf-of, <c>act</c>) and claim-less tokens are refused with message 144 and nothing is persisted.
///     Unrestricted types keep accepting every caller. Covered surfaces: GraphQL mutations, the REST session (the
///     security context Files/LargeBinaries build) and the ImportRt user path (the caller context the job builds from the
///     kind the controller sends).
/// </summary>
[Collection(GraphQlMutatingCollection.Name)]
public class PrincipalKindWriteAccessTests
{
    private const string RestrictedCkTypeId = "AssetRepositoryIntegrationTest/InteractiveOnlyNote";
    private const string UnrestrictedCkTypeId = "AssetRepositoryIntegrationTest/Customer";
    private const string RefusedMessageNumber = "144";

    private const string CreateMutation = """
        mutation ($entities: [RtEntityInput!]!) {
          runtime { runtimeEntities { create(entities: $entities) { rtId } } }
        }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly GraphQlTestFixture _fixture;

    public PrincipalKindWriteAccessTests(GraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    // Token shapes as the identity service issues them (claim names only; see CallerPrincipalKind).
    private static ClaimsPrincipal Token(params (string Type, string Value)[] claims)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Bearer"));
    }

    public static TheoryData<string> InteractiveTokens => ["interactive", "device-code", "token-exchange"];
    public static TheoryData<string> RefusedTokens => ["client-credentials", "delegated", "impersonated", "no-claim"];

    private static ClaimsPrincipal TokenOf(string shape)
    {
        return shape switch
        {
            // authorization code login (Studio): sub + amr pwd
            "interactive" => Token(("sub", "65f0c0ffee65f0c0ffee7001"), ("amr", "pwd"), ("role", "TenantManagement")),
            // device code (octo-cli, MCP desktop): the same user claims, amr of the session
            "device-code" => Token(("sub", "65f0c0ffee65f0c0ffee7002"), ("amr", "mfa"), ("role", "TenantManagement")),
            // cross-tenant exchange of a person's own token: shadow-user sub, amr token_exchange, no actor
            "token-exchange" => Token(("sub", "65f0c0ffee65f0c0ffee7003"), ("amr", "token_exchange"),
                ("role", "TenantManagement")),
            // client credentials: no sub by platform contract, client_id
            "client-credentials" => Token(("client_id", "claude-agent-test"), ("role", "TenantManagement")),
            // on-behalf-of (mcpDelegateToCaller): user sub + act = service account, amr delegation
            "delegated" => Token(("sub", "65f0c0ffee65f0c0ffee7004"), ("act", "pipeline-sa"), ("amr", "delegation"),
                ("client_id", "pipeline-sa"), ("role", "TenantManagement")),
            // impersonation: client-shaped for the target, act = actor
            "impersonated" => Token(("client_id", "target-sa"), ("act", "actor-sa"), ("amr", "impersonation"),
                ("role", "TenantManagement")),
            // authenticated, but neither sub nor client_id
            "no-claim" => Token(("scope", "assetSystemAPI"), ("role", "TenantManagement")),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
        };
    }

    [Theory]
    [MemberData(nameof(InteractiveTokens))]
    public async Task GraphQl_UserToken_WritesTheRestrictedType(string shape)
    {
        var name = $"Note_{shape}_{Guid.NewGuid():N}";

        var result = await _fixture.ExecuteGraphQlAsync(CreateMutation, NoteVariables(name), TokenOf(shape));

        result.Errors.Should().BeNullOrEmpty();
        (await ExistsAsync(RestrictedCkTypeId, name)).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(RefusedTokens))]
    public async Task GraphQl_ClientDelegatedOrClaimlessToken_IsRefusedWith144_AndNothingIsPersisted(string shape)
    {
        var name = $"Note_{shape}_{Guid.NewGuid():N}";

        var result = await _fixture.ExecuteGraphQlAsync(CreateMutation, NoteVariables(name), TokenOf(shape));

        var errors = JArray.Parse(JObject.Parse(_fixture.SerializeGraphQl(result))["errors"]!.ToString());
        errors.Should().ContainSingle();
        errors[0].SelectToken("extensions.code")!.Value<string>().Should().Be("ASSET1004");
        var details = errors.SelectTokens("$..extensions.OctoDetails[*].message").Select(t => t.Value<string>())
            .ToList();
        details.Should().Contain(d => d!.StartsWith(RefusedMessageNumber + ":") && d.Contains("writeAccess"),
            "the engine refuses the write with the stable message 144");
        // the session carried the kind the asset repository derived from the token
        var expectedKind = shape == "no-claim" ? RtPrincipalKind.Unknown : RtPrincipalKind.Client;
        details.Should().Contain(d => d!.Contains($"principal '{expectedKind}'"));
        (await ExistsAsync(RestrictedCkTypeId, name)).Should().BeFalse("a refused write persists nothing");
    }

    [Theory]
    [MemberData(nameof(RefusedTokens))]
    [MemberData(nameof(InteractiveTokens))]
    public async Task GraphQl_AnyToken_StillWritesUnrestrictedTypes(string shape)
    {
        var name = $"Customer_{shape}_{Guid.NewGuid():N}";
        var variables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = UnrestrictedCkTypeId,
                    rtWellKnownName = name,
                    attributes = new[]
                    {
                        new { attributeName = "firstName", value = "Principal" },
                        new { attributeName = "lastName", value = "Kind" },
                        new { attributeName = "street", value = "Test Street 1" },
                        new { attributeName = "postalCode", value = "12345" },
                        new { attributeName = "city", value = "Test City" },
                        new { attributeName = "country", value = "Austria" }
                    }
                }
            }
        });

        var result = await _fixture.ExecuteGraphQlAsync(CreateMutation, variables, TokenOf(shape));

        result.Errors.Should().BeNullOrEmpty();
        (await ExistsAsync(UnrestrictedCkTypeId, name)).Should().BeTrue();
    }

    [Theory]
    [InlineData("interactive", true)]
    [InlineData("device-code", true)]
    [InlineData("client-credentials", false)]
    [InlineData("delegated", false)]
    [InlineData("no-claim", false)]
    public async Task Rest_SessionOfTheCaller_AdmitsOnlyUserTokens(string shape, bool admitted)
    {
        // The security context every REST surface builds (FilesController, LargeBinariesController).
        var caller = Helpers.GetSecurityContext(TokenOf(shape));
        var repository = _fixture.GetSystemContext().GetSystemTenantRepository();
        var ckTypeId = new RtCkId<CkTypeId>(RestrictedCkTypeId);
        var name = $"Note_rest_{shape}_{Guid.NewGuid():N}";
        var entity = new RtEntity(ckTypeId, OctoObjectId.GenerateNewId(),
            new Dictionary<string, object?> { { "NoteText", "via REST" } }) { RtWellKnownName = name };

        using var session = repository.GetSession(caller);
        session.StartTransaction();
        var apply = () => repository.ApplyChangesAsync(session,
            [EntityUpdateInfo<RtEntity>.CreateInsert(ckTypeId, entity)], [], new OperationResult());
        if (admitted)
        {
            await apply();
            await session.CommitTransactionAsync();
        }
        else
        {
            var exception = (await apply.Should().ThrowAsync<RuntimeRepositoryException>()).Which;
            exception.OperationResult.Messages.Select(m => m.MessageNumber.ToString())
                .Should().Contain(RefusedMessageNumber);
            await session.AbortTransactionAsync();
        }

        (await ExistsAsync(RestrictedCkTypeId, name)).Should().Be(admitted);
    }

    [Theory]
    [InlineData("interactive", true)]
    [InlineData("device-code", true)]
    [InlineData("client-credentials", false)]
    [InlineData("delegated", false)]
    [InlineData("no-claim", false)]
    public async Task ImportRt_UserPath_AdmitsOnlyUserTokens(string shape, bool admitted)
    {
        // What the controller sends (subject + principal kind from the token) and the job hands to the engine.
        var caller = Helpers.GetSecurityContext(TokenOf(shape));
        var rtId = OctoObjectId.GenerateNewId().ToString();
        var name = $"Note_import_{shape}_{Guid.NewGuid():N}";
        var file = Path.Combine(Path.GetTempPath(), $"principal-kind-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(file, $"""
            $schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
            dependencies:
              - AssetRepositoryIntegrationTest
            entities:
              - rtId: '{rtId}'
                ckTypeId: {RestrictedCkTypeId}
                rtWellKnownName: '{name}'
                attributes:
                  - id: AssetRepositoryIntegrationTest/NoteText
                    value: via ImportRt
            """, Encoding.UTF8, Ct);

        var command = _fixture.GetService<IImportRtModelCommand>();
        var repository = _fixture.GetSystemContext().GetSystemTenantRepository();
        try
        {
            var import = () => command.ImportAsCallerAsync(repository, file, ExchangeMimeTypes.MimeTypeYaml,
                ImportStrategy.Upsert, RtImportCallerContext.UserInitiated(caller.SubjectId, caller.PrincipalKind), Ct);
            if (admitted)
            {
                await import();
            }
            else
            {
                var exception = (await import.Should().ThrowAsync<ExchangeException>()).Which;
                exception.MessageNumber.Should().Be(int.Parse(RefusedMessageNumber));
            }
        }
        finally
        {
            File.Delete(file);
        }

        (await ExistsAsync(RestrictedCkTypeId, name)).Should().Be(admitted);
    }

    private static string NoteVariables(string name)
    {
        return JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = RestrictedCkTypeId,
                    rtWellKnownName = name,
                    attributes = new[] { new { attributeName = "noteText", value = "written by a token" } }
                }
            }
        });
    }

    private async Task<bool> ExistsAsync(string ckTypeId, string wellKnownName)
    {
        // Straight from MongoDB, independent of the engine's read path: is anything persisted under this name?
        var suffix = ckTypeId.Replace("/", string.Empty);
        var collection = new MongoClient(_fixture.GetConnectionString()).GetDatabase(_fixture.SystemDatabaseName)
            .GetCollection<BsonDocument>($"RtEntity_{suffix}");
        return await collection.Find(new BsonDocument("rtWellKnownName", wellKnownName)).AnyAsync(Ct);
    }
}
