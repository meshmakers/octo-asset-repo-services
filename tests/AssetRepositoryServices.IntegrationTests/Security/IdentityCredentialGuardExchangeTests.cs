using System.Text;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer.DTOs;
using Xunit;
using F = Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures.IdentityCredentialGuardFixture;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Security;

/// <summary>
///     Credential guard regression tests, RT exchange: an RT export (by query and by deep graph) never carries the
///     user's credentials, and an RT import (system path and caller path, YAML file) refuses every value for a Hidden or
///     MethodOnly identity attribute before anything is written. The export runs the same engine building blocks as the
///     export jobs of the job service (repository read, <see cref="IRtEntityToTcDtoConverter" />, <see cref="IRtSerializer" />)
///     against the real System.Identity model, with a system session - the session that sees everything.
/// </summary>
[Collection(IdentityCredentialGuardCollection.Name)]
public class IdentityCredentialGuardExchangeTests
{
    private const string RefusedMessageNumber = "139";

    private readonly F _fixture;
    private readonly int _logStart;

    public IdentityCredentialGuardExchangeTests(F fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
        _logStart = _fixture.LogCount;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------------------------------------- export

    [Fact]
    public async Task ExportByQuery_OfAllUsers_CarriesNoCredential()
    {
        var repository = _fixture.GetSystemContext().GetSystemTenantRepository();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var users = await repository.GetRtEntitiesByTypeAsync(session, new RtCkId<CkTypeId>(F.UserCkTypeId),
            RtEntityQueryOptions.Create());
        await session.CommitTransactionAsync();

        var model = new RtModelRootTcDto();
        var converter = _fixture.GetService<IRtEntityToTcDtoConverter>();
        model.Entities.AddRange(users.Items.Select(u => converter.Convert(_fixture.SystemTenantId, u)));

        var file = await SerializeAsync(model);

        file.Should().Contain(F.ProbeUserName, "positive control: the user is exported");
        AssertNoCredential(file);
    }

    [Fact]
    public async Task ExportByDeepGraph_FromTheRole_CarriesNoCredential()
    {
        var repository = _fixture.GetSystemContext().GetSystemTenantRepository();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var graph = await repository.GetRtDeepGraphAsync(session, [new OctoObjectId(_fixture.ProbeRoleRtId)],
            new RtCkId<CkTypeId>(F.RoleCkTypeId), RtEntityQueryOptions.Create());

        var model = new RtModelRootTcDto();
        var converter = _fixture.GetService<IRtEntityToTcDtoConverter>();
        foreach (var group in graph.Items.GroupBy(i => i.Id.CkTypeId))
        {
            var entities = await repository.GetRtEntitiesByIdAsync(session, group.Key,
                group.Select(i => i.Id.RtId).ToList(), RtEntityQueryOptions.Create());
            model.Entities.AddRange(entities.Items.Select(e => converter.Convert(_fixture.SystemTenantId, e)));
        }

        await session.CommitTransactionAsync();

        var file = await SerializeAsync(model);

        file.Should().Contain(F.ProbeUserName, "positive control: the deep graph reaches the user over AssignedRole");
        AssertNoCredential(file);
    }

    // ------------------------------------------------------------------------------------------------- import

    public static TheoryData<string, string, bool> RestrictedImportValues() => new()
    {
        // attribute id, YAML value, caller path
        { "System.Identity/PasswordHash", F.DummyPasswordHash, false },
        { "System.Identity/PasswordHash", F.DummyPasswordHash, true },
        { "System.Identity/SecurityStamp", F.DummySecurityStamp, false },
        { "System.Identity/SecurityStamp", F.DummySecurityStamp, true },
        { "System.Identity/UserTokens", TokensYaml, false },
        { "System.Identity/UserTokens", TokensYaml, true },
        { "System.Identity/UserName", "imported-user-name", false },
        { "System.Identity/UserName", "imported-user-name", true },
        { "System.Identity/Email", "imported@example.invalid", false },
        { "System.Identity/Email", "imported@example.invalid", true }
    };

    [Theory]
    [MemberData(nameof(RestrictedImportValues))]
    public async Task ImportRt_OfANewUser_WithARestrictedValue_IsRefused_AndWritesNothing(string attributeId,
        string value, bool callerPath)
    {
        var rtId = OctoObjectId.GenerateNewId().ToString();
        var file = WriteYaml(UserYaml(rtId, attributeId, value));

        var exception = await ImportAsync(file, ImportStrategy.Insert, callerPath);

        AssertRefusal(exception, attributeId);
        (await FindUserAsync(rtId)).Should().BeFalse("nothing is written");
    }

    [Theory]
    [InlineData("System.Identity/PasswordHash", false)]
    [InlineData("System.Identity/SecurityStamp", true)]
    [InlineData("System.Identity/UserName", false)]
    public async Task ImportRt_Upsert_OfTheExistingUser_WithARestrictedValue_IsRefused_AndChangesNothing(
        string attributeId, bool callerPath)
    {
        var file = WriteYaml(UserYaml(_fixture.ProbeUserRtId, attributeId, "OVERWRITTEN-BY-IMPORT"));

        var exception = await ImportAsync(file, ImportStrategy.Upsert, callerPath);

        AssertRefusal(exception, attributeId);
        (await _fixture.ReadRawAttributeValueFromMongoDb(_fixture.ProbeUserRtId, "passwordHash", F.UserCollectionSuffix))
            .AsString.Should().Be(F.DummyPasswordHash);
        (await _fixture.ReadRawAttributeValueFromMongoDb(_fixture.ProbeUserRtId, "securityStamp", F.UserCollectionSuffix))
            .AsString.Should().Be(F.DummySecurityStamp);
        (await _fixture.ReadRawAttributeValueFromMongoDb(_fixture.ProbeUserRtId, "userName", F.UserCollectionSuffix))
            .AsString.Should().Be(F.ProbeUserName);
    }

    [Fact]
    public async Task ImportRt_OfAUser_WithVisibleValuesOnly_IsAccepted_PositiveControl()
    {
        var rtId = OctoObjectId.GenerateNewId().ToString();
        var file = WriteYaml(UserYaml(rtId, "System.Identity/FirstName", "visible-only"));

        (await ImportAsync(file, ImportStrategy.Insert, callerPath: false)).Should().BeNull();
        (await FindUserAsync(rtId)).Should().BeTrue();
    }

    // AB#8042: a user-initiated import (caller path) cannot set an identity-owned User field or write an identity association
    // (engine message 140); the system path (blueprint seed) is unchanged (positive control above imports the same fields).
    [Theory]
    [InlineData("System.Identity/TwoFactorEnabled", "true")]
    [InlineData("System.Identity/EmailConfirmed", "true")]
    [InlineData("System.Identity/NormalizedUserName", "'OTHER-USER'")]
    [InlineData("System.Identity/ResetPasswordOnLogin", "false")]
    [InlineData("System.Identity/LockoutEnd", "'2000-01-01T00:00:00Z'")] // AB#8059: a caller import cannot reset a lockout
    [InlineData("System.Identity/AccessFailedCount", "0")]
    public async Task ImportRt_CallerPath_IdentityOwnedField_IsRefused_AndWritesNothing(string attributeId, string value)
    {
        var rtId = OctoObjectId.GenerateNewId().ToString();
        var file = WriteYaml(PlainUserYaml(rtId, $"""
                  - id: {attributeId}
                    value: {value}
            """));

        var exception = await ImportAsync(file, ImportStrategy.Insert, callerPath: true);

        AssertIdentityOwnedRefusal(exception, attributeId.Split('/')[1]);
        (await FindUserAsync(rtId)).Should().BeFalse("nothing is written");
    }

    [Fact]
    public async Task ImportRt_CallerPath_RoleAssignment_IsRefused_AndChangesNothing()
    {
        var file = WriteYaml(PlainUserYaml(_fixture.ProbeUserRtId, string.Empty, $"""
                associations:
                  - roleId: {F.AssignedRoleId}
                    targetRtId: '{_fixture.ProbeRoleRtId}'
                    targetCkTypeId: {F.RoleCkTypeId}
            """));

        var exception = await ImportAsync(file, ImportStrategy.Upsert, callerPath: true);

        AssertIdentityOwnedRefusal(exception, "AssignedRole");
        (await _fixture.ReadRawAttributeValueFromMongoDb(_fixture.ProbeUserRtId, "firstName", F.UserCollectionSuffix))
            .Should().NotBe(new MongoDB.Bson.BsonString("caller-import-8042"), "nothing is written");
    }

    [Fact]
    public async Task ImportRt_CallerPath_OtherUserFields_AreAccepted_PositiveControl()
    {
        var rtId = OctoObjectId.GenerateNewId().ToString();
        var file = WriteYaml(PlainUserYaml(rtId, string.Empty));

        (await ImportAsync(file, ImportStrategy.Insert, callerPath: true)).Should().BeNull();
        (await FindUserAsync(rtId)).Should().BeTrue();
    }

    private static string PlainUserYaml(string rtId, string extraAttributes, string associations = "") => $"""
        $schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
        dependencies:
          - System.Identity
        entities:
          - rtId: '{rtId}'
            ckTypeId: {F.UserCkTypeId}
            attributes:
              - id: System.Identity/FirstName
                value: caller-import-8042
        {extraAttributes}{associations}
        """;

    private void AssertIdentityOwnedRefusal(ExchangeException? exception, string pathPart)
    {
        exception.Should().NotBeNull("the import must be refused");
        exception!.MessageNumber.Should().Be(140);
        exception.Messages.Should().NotBeEmpty().And.OnlyContain(m => m.MessageNumber == 140);
        exception.Messages.Should().Contain(m => m.Parameters["path"].Contains(pathPart));
        exception.Message.Should().Contain("only the identity service writes");
        AssertNoCredential(exception.ToString());
    }

    // ------------------------------------------------------------------------------------------------- helpers

    private const string TokensYaml = "TOKENS";

    private static string UserYaml(string rtId, string attributeId, string value)
    {
        var valueYaml = value == TokensYaml
            ? $"""

                        - ckRecordId: {F.UserTokenCkRecordId}
                          attributes:
                            - id: System.Identity/LoginProvider
                              value: '[AspNetUserStore]'
                            - id: System/Name
                              value: AuthenticatorKey
                            - id: System.Identity/UserId
                              value: imported
                            - id: System.Identity/Value
                              value: {F.DummyTokenValue}
              """
            : $" '{value}'";
        return $"""
            $schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
            dependencies:
              - System.Identity
            entities:
              - rtId: '{rtId}'
                ckTypeId: {F.UserCkTypeId}
                attributes:
                  - id: System.Identity/EmailConfirmed
                    value: false
                  - id: System.Identity/PhoneNumberConfirmed
                    value: false
                  - id: System.Identity/TwoFactorEnabled
                    value: false
                  - id: System.Identity/LockoutEnabled
                    value: true
                  - id: System.Identity/AccessFailedCount
                    value: 0
                  - id: System.Identity/ResetPasswordOnLogin
                    value: false
                  - id: {attributeId}
                    value:{valueYaml}
            """;
    }

    private static string WriteYaml(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"credential-guard-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml, Encoding.UTF8);
        return path;
    }

    private async Task<ExchangeException?> ImportAsync(string file, ImportStrategy strategy, bool callerPath)
    {
        var command = _fixture.GetService<IImportRtModelCommand>();
        var repository = _fixture.GetSystemContext().GetSystemTenantRepository();
        try
        {
            if (callerPath)
            {
                await command.ImportAsCallerAsync(repository, file, ExchangeMimeTypes.MimeTypeYaml, strategy,
                    RtImportCallerContext.UserInitiated("65f0c0ffee65f0c0ffee6501"), Ct);
            }
            else
            {
                await command.ImportAsync(repository, file, ExchangeMimeTypes.MimeTypeYaml, strategy, Ct);
            }

            return null;
        }
        catch (ExchangeException e)
        {
            return e;
        }
        finally
        {
            File.Delete(file);
        }
    }

    private void AssertRefusal(ExchangeException? exception, string attributeId)
    {
        exception.Should().NotBeNull("the import must be refused");
        exception!.MessageNumber.Should().Be(int.Parse(RefusedMessageNumber));
        exception.Message.Should().Contain(attributeId.Split('/')[1]);
        AssertNoCredential(exception.ToString());
        exception.Message.Should().NotContain("OVERWRITTEN-BY-IMPORT");
        _fixture.LogLinesWithDummyValuesSince(_logStart).Should().BeEmpty("no log line may carry a credential");
    }

    private async Task<bool> FindUserAsync(string rtId)
    {
        var repository = _fixture.GetSystemContext().GetSystemTenantRepository();
        using var session = await repository.GetSessionAsync();
        var entity = await repository.GetRtEntityByRtIdAsync(session,
            new RtEntityId(new RtCkId<CkTypeId>(F.UserCkTypeId), new OctoObjectId(rtId)));
        return entity != null;
    }

    private async Task<string> SerializeAsync(RtModelRootTcDto model)
    {
        var path = Path.Combine(Path.GetTempPath(), $"credential-guard-export-{Guid.NewGuid():N}.json");
        try
        {
            await using (var writer = new StreamWriter(path))
            {
                await _fixture.GetService<IRtSerializer>().SerializeAsync(writer, model);
            }

            return await File.ReadAllTextAsync(path, Ct);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private void AssertNoCredential(string text)
    {
        foreach (var dummy in F.DummyValues)
        {
            text.Should().NotContain(dummy);
        }

        _fixture.LogLinesWithDummyValuesSince(_logStart).Should().BeEmpty("no log line may carry a credential");
    }
}
