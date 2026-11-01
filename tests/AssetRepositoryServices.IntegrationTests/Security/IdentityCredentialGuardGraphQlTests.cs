using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;
using F = Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures.IdentityCredentialGuardFixture;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Security;

/// <summary>
///     Credential guard regression tests, GraphQL surface: every GraphQL path refuses to read or write the user's
///     <c>PasswordHash</c>, <c>SecurityStamp</c> and <c>UserTokens</c> (access Hidden) and refuses generic writes of the
///     method-only / read-only identity attributes, against the real <c>System.Identity</c> model. The seeded user holds
///     known dummy values (<see cref="F.DummyValues" />); no response, error or captured log line may contain them.
///     The tests prove the generic access guards for the identity model; the guards themselves are covered with a test
///     model in <c>GraphQL/CkV2/HiddenAttribute*Tests</c>.
/// </summary>
[Collection(IdentityCredentialGuardCollection.Name)]
public class IdentityCredentialGuardGraphQlTests
{
    private static readonly string[] HiddenAttributes = ["passwordHash", "securityStamp", "userTokens"];

    private readonly F _fixture;
    private readonly int _logStart;

    public IdentityCredentialGuardGraphQlTests(F fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
        _fixture.Dispatcher.Reset();
        // Tests of one collection run one after the other: every log line after this index belongs to this test.
        _logStart = _fixture.LogCount;
    }

    // ------------------------------------------------------------------------------------------------- schema / meta

    [Fact]
    public async Task Schema_HidesTheCredentials_AndKeepsMethodOnlyAttributesOutOfTheInputs()
    {
        var output = await FieldNamesAsync(F.UserTypeName, "fields");
        var input = await FieldNamesAsync(F.UserTypeName + "Input", "inputFields");
        var update = await FieldNamesAsync(F.UserTypeName + "InputUpdate", "inputFields");

        output.Should().Contain(["userName", "email", "lockoutEnd", "accessFailedCount"]);
        output.Should().NotContain(HiddenAttributes);
        input.Should().NotContain(HiddenAttributes).And.NotContain(["userName", "email"]);
        update.Should().NotContain(HiddenAttributes).And.NotContain(["userName", "email"]);
    }

    [Fact]
    public async Task CkMeta_ReportsTheAccessOfTheIdentityAttributes()
    {
        var json = await ExecuteOkAsync($$"""
            query { constructionKit { types(rtCkId: "{{F.UserCkTypeId}}") {
              items { attributes(first: 100) { items { attributeName access } } } } } }
            """);

        var access = JObject.Parse(json).SelectTokens("data.constructionKit.types.items[0].attributes.items[*]")
            .ToDictionary(a => a["attributeName"]!.Value<string>()!, a => a["access"]!.Value<string>());
        access["passwordHash"].Should().Be("Hidden");
        access["securityStamp"].Should().Be("Hidden");
        access["userTokens"].Should().Be("Hidden");
        access["userName"].Should().Be("MethodOnly");
        access["email"].Should().Be("MethodOnly");
        access["lockoutEnd"].Should().Be("ReadOnly");
        access["accessFailedCount"].Should().Be("ReadOnly");
    }

    [Fact]
    public async Task AvailableQueryColumns_DoNotOfferTheCredentials()
    {
        var json = await ExecuteOkAsync($$"""
            query { constructionKit { types(rtCkId: "{{F.UserCkTypeId}}") {
              items { availableQueryColumns(first: 500) { items { attributePath } } } } } }
            """);

        var paths = JObject.Parse(json).SelectTokens("$..attributePath").Select(t => t.Value<string>()!).ToList();
        paths.Should().Contain("userName");
        paths.Should().NotContain(p => HiddenAttributes.Any(h => p.Contains(h, StringComparison.OrdinalIgnoreCase)));
    }

    // ------------------------------------------------------------------------------------------------- reads

    [Fact]
    public async Task TypedQuery_WithEveryVisibleField_ReturnsNoCredential()
    {
        var fields = (await FieldNamesAsync(F.UserTypeName, "fields"))
            .Where(f => !f.StartsWith("__", StringComparison.Ordinal)).ToList();
        var scalarFields = await ScalarFieldNamesAsync(F.UserTypeName);

        var json = await ExecuteOkAsync($$"""
            query { runtime { {{F.UserQueryField}}(rtIds: ["{{_fixture.ProbeUserRtId}}"]) {
              items { {{string.Join(" ", fields.Where(scalarFields.Contains))}} } } } }
            """);

        json.Should().Contain(F.ProbeUserName, "positive control: the user is found");
        AssertNoDummyValue(json);
    }

    [Fact]
    public async Task TypedQuery_SelectingAHiddenField_IsAValidationError_WithoutValue()
    {
        foreach (var hidden in HiddenAttributes)
        {
            var result = await _fixture.ExecuteGraphQlAsync($$"""
                query { runtime { {{F.UserQueryField}}(rtIds: ["{{_fixture.ProbeUserRtId}}"]) { items { {{hidden}} } } } }
                """);

            result.Errors.Should().NotBeNullOrEmpty($"{hidden} is no field of the user");
            AssertNoDummyValue(_fixture.SerializeGraphQl(result));
        }
    }

    [Fact]
    public async Task GenericAttributeList_ReturnsNoCredential()
    {
        var json = await ExecuteOkAsync($$"""
            query { runtime { runtimeEntities(ckId: "{{F.UserCkTypeId}}", rtIds: ["{{_fixture.ProbeUserRtId}}"]) {
              items { rtId attributes(first: 100) { items { attributeName value } } } } } }
            """);

        json.Should().Contain(F.ProbeUserName, "positive control");
        AssertNoDummyValue(json);
        AssertNoDummyValueInTestLogs();
        foreach (var hidden in HiddenAttributes)
        {
            json.Should().NotContain($"\"{hidden}\"");
        }
    }

    [Fact]
    public async Task GenericAssociationTarget_FromTheRole_ReturnsNoCredential()
    {
        var json = await ExecuteOkAsync($$"""
            query { runtime { runtimeEntities(ckId: "{{F.RoleCkTypeId}}", rtIds: ["{{_fixture.ProbeRoleRtId}}"]) {
              items { associations { targets(roleId: "{{F.AssignedRoleId}}", direction: INBOUND, ckId: "{{F.UserCkTypeId}}") {
                items { rtId attributes(first: 100) { items { attributeName value } } } } } } } } }
            """);

        json.Should().Contain(F.ProbeUserName, "positive control: the association is followed");
        AssertNoDummyValue(json);
    }

    public static TheoryData<string, string> HiddenFilters()
    {
        var data = new TheoryData<string, string>();
        foreach (var path in new[] { "passwordHash", "securityStamp", "userTokens", "userTokens.value" })
        {
            foreach (var op in new[] { "EQUALS", "NOT_EQUALS", "LIKE", "MATCH_REG_EX", "LESS_THAN", "GREATER_EQUAL_THAN", "IN", "IS_NULL", "IS_NOT_NULL" })
            {
                data.Add(path, op);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(HiddenFilters))]
    public async Task TypedQuery_FilterOnACredential_IsRefused(string path, string op)
    {
        var comparison = op switch
        {
            "IS_NULL" or "IS_NOT_NULL" => string.Empty,
            "IN" => $", comparisonValue: [\"{F.DummyPasswordHash}\"]",
            _ => $", comparisonValue: \"{F.DummyPasswordHash}\""
        };

        await AssertRefusedAsync($$"""
            query { runtime { {{F.UserQueryField}}(fieldFilter: [{ attributePath: "{{path}}", operator: {{op}}{{comparison}} }]) {
              totalCount items { rtId } } } }
            """, "ATTRIBUTE_NOT_QUERYABLE");
    }

    [Theory]
    [InlineData("passwordHash")]
    [InlineData("securityStamp")]
    [InlineData("userTokens.value")]
    public async Task GenericQuery_FilterOnACredential_IsRefused(string path)
    {
        await AssertRefusedAsync($$"""
            query { runtime { runtimeEntities(ckId: "{{F.UserCkTypeId}}",
              fieldFilter: [{ attributePath: "{{path}}", operator: EQUALS, comparisonValue: "{{F.DummyPasswordHash}}" }]) {
              totalCount items { rtId } } } }
            """, "ATTRIBUTE_NOT_QUERYABLE");
    }

    [Theory]
    [InlineData("passwordHash")]
    [InlineData("securityStamp")]
    [InlineData("userTokens")]
    public async Task TypedQuery_SortOnACredential_IsRefused(string path)
    {
        await AssertRefusedAsync($$"""
            query { runtime { {{F.UserQueryField}}(sortOrder: [{ attributePath: "{{path}}", sortOrder: ASCENDING }]) {
              totalCount items { rtId } } } }
            """, "ATTRIBUTE_NOT_QUERYABLE");
    }

    [Theory]
    [InlineData("""searchFilter: { type: ATTRIBUTE_FILTER, attributePaths: ["passwordHash"], searchTerm: "DUMMY" }""")]
    [InlineData("""searchFilter: { type: ATTRIBUTE_FILTER, attributePaths: ["securityStamp"], searchTerm: "DUMMY" }""")]
    [InlineData("""searchFilter: { type: ATTRIBUTE_FILTER, attributePaths: ["userTokens.value"], searchTerm: "DUMMY" }""")]
    [InlineData("""aggregations: { maxValueAttributePaths: ["passwordHash"] }""")]
    [InlineData("""aggregations: { minValueAttributePaths: ["securityStamp"] }""")]
    [InlineData("""aggregations: { countAttributePaths: ["userTokens"] }""")]
    [InlineData("""aggregations: { groupBy: { groupByAttributePaths: ["passwordHash"] } }""")]
    [InlineData("""aggregations: { groupBy: { groupByAttributePaths: ["securityStamp"] } }""")]
    public async Task TypedQuery_SearchOrAggregationOnACredential_IsRefused(string argument)
    {
        await AssertRefusedAsync($$"""
            query { runtime { {{F.UserQueryField}}({{argument}}) { totalCount items { rtId } } } }
            """, "ATTRIBUTE_NOT_QUERYABLE");
    }

    [Theory]
    [InlineData("passwordHash")]
    [InlineData("securityStamp")]
    [InlineData("userTokens")]
    [InlineData("userTokens.value")]
    public async Task TransientQuery_CredentialColumn_IsRefused(string column)
    {
        await AssertRefusedAsync($$"""
            query { runtime { transientQuery {
              simple(ckId: "{{F.UserCkTypeId}}", columnPaths: ["userName", "{{column}}"]) {
                items { rows { items { ... on RtSimpleQueryRow { cells { items { attributePath value } } } } } } } } } }
            """, "ATTRIBUTE_NOT_QUERYABLE");
    }

    [Theory]
    [InlineData("passwordHash", "firstName")]
    [InlineData("securityStamp", "firstName")]
    [InlineData("firstName", "passwordHash")]
    [InlineData("firstName", "securityStamp")]
    public async Task TransientQuery_GroupingAggregationOverACredential_IsRefused(string groupBy, string aggregated)
    {
        await AssertRefusedAsync($$"""
            query { runtime { transientQuery {
              groupingAggregation(ckId: "{{F.UserCkTypeId}}", groupByColumnPaths: ["{{groupBy}}"],
                                  columnPaths: [{ attributePath: "{{aggregated}}", aggregationType: COUNT }]) {
                items { rows { items { ... on RtGroupingAggregationQueryRow { cells { items { attributePath value } } } } } } } } } }
            """, "ATTRIBUTE_NOT_QUERYABLE");
    }

    [Fact]
    public async Task EntitySelector_VisibleKey_ResolvesTheUser_PositiveControl()
    {
        var json = await ExecuteOkAsync(SelectorQuery($"assignedEntities.systemIdentityUser[userName='{F.ProbeUserName}']->userName"));

        json.Should().Contain(F.ProbeUserName, "the selector form is a real, resolvable column");
    }

    [Theory]
    [InlineData("passwordHash", F.DummyPasswordHash)]
    [InlineData("securityStamp", F.DummySecurityStamp)]
    public async Task EntitySelector_EqualityProbeOnACredential_IsRefused(string key, string guess)
    {
        await AssertRefusedAsync(SelectorQuery($"assignedEntities.systemIdentityUser[{key}='{guess}']->userName"),
            "ATTRIBUTE_NOT_QUERYABLE");
    }

    [Fact]
    public async Task EntitySelector_InAStoredQuery_IsRefused()
    {
        // AB#8049: the query is refused when it is saved (before, saving was accepted and only running it was refused).
        var result = await _fixture.ExecuteGraphQlAsync(GenericCreateMutation, JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = "System/SimpleRtQuery",
                    attributes = new object[]
                    {
                        new { attributeName = "name", value = (object)"credential-selector" },
                        new { attributeName = "queryCkTypeId", value = (object)F.RoleCkTypeId },
                        new
                        {
                            attributeName = "columns",
                            value = (object)new[]
                                { "name", $"assignedEntities.systemIdentityUser[passwordHash='{StoredGuess}']->userName" }
                        }
                    }
                }
            }
        }));

        AssertRefused(result, "ATTRIBUTE_NOT_QUERYABLE");
        _fixture.SerializeGraphQl(result).Should().NotContain(StoredGuess);
    }

    [Theory]
    [InlineData("passwordHash")]
    [InlineData("securityStamp")]
    [InlineData("userTokens.value")]
    public async Task StoredQuery_WithACredentialFilter_IsRefusedWhenSaved(string path)
    {
        // AB#8049: saving a query whose filter reaches a credential is refused (ATTRIBUTE_NOT_QUERYABLE). Should a query get in
        // another way (import, restore), running it must not apply the filter either.
        var create = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { systemSimpleRtQuerys { create(entities: [{
              name: "credential-filter", queryCkTypeId: "{{F.UserCkTypeId}}", columns: ["userName"],
              fieldFilter: [{ attributePath: "{{path}}", operator: EQUALS, comparisonValue: "{{StoredGuess}}" }]
            }]) { rtId } } } }
            """);
        var createJson = _fixture.SerializeGraphQl(create);
        AssertNoDummyValue(createJson);
        create.Errors.Should().NotBeNullOrEmpty("a stored credential filter is refused when saved (AB#8049)");
        create.Errors!.Select(e => e.Code).Should().Contain("ATTRIBUTE_NOT_QUERYABLE", createJson);
    }

    // ------------------------------------------------------------------------------------------------- writes

    public static TheoryData<string, object> RestrictedWrites() => new()
    {
        { "passwordHash", F.DummyPasswordHash },
        { "securityStamp", F.DummySecurityStamp },
        { "userName", "renamed-by-generic-write" },
        { "email", "redirected@example.invalid" }
    };

    [Theory]
    [MemberData(nameof(RestrictedWrites))]
    public async Task GenericCreate_OfAUser_WithARestrictedAttribute_IsRefused(string attribute, object value)
    {
        var result = await _fixture.ExecuteGraphQlAsync(GenericCreateMutation, JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = F.UserCkTypeId,
                    attributes = new object[]
                    {
                        new { attributeName = "firstName", value = (object)"generic-create" },
                        new { attributeName = attribute, value }
                    }
                }
            }
        }));

        AssertRefused(result, "ATTRIBUTE_NOT_WRITABLE");
    }

    [Fact]
    public async Task GenericCreate_OfAUser_WithUserTokens_IsRefused()
    {
        var result = await _fixture.ExecuteGraphQlAsync(GenericCreateMutation, JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = F.UserCkTypeId,
                    attributes = new object[]
                    {
                        new { attributeName = "firstName", value = (object)"generic-create-tokens" },
                        new { attributeName = "userTokens", value = (object)TokenRecords() }
                    }
                }
            }
        }));

        AssertRefused(result, "ATTRIBUTE_NOT_WRITABLE");
    }

    [Theory]
    [MemberData(nameof(RestrictedWrites))]
    public async Task GenericUpdate_OfTheUser_WithARestrictedAttribute_IsRefused_AndNothingChanges(string attribute,
        object value)
    {
        var result = await GenericUpdateAsync(_fixture.ProbeUserRtId, attribute, value);

        AssertRefused(result, "ATTRIBUTE_NOT_WRITABLE");
        await AssertProbeUserUnchangedAsync();
    }

    // Open gap: ReadOnly is not enforced on the generic update path yet. Enable with the fix.
    [Theory(Skip = "AB#8047: the generic update writes ReadOnly attributes")]
    [InlineData("lockoutEnd", "2020-01-01T00:00:00Z")]
    [InlineData("accessFailedCount", 0)]
    public async Task GenericUpdate_OfAReadOnlyLockoutAttribute_IsRefused(string attribute, object value)
    {
        var result = await GenericUpdateAsync(_fixture.ProbeUserRtId, attribute, value);

        AssertRefused(result, "ATTRIBUTE_NOT_WRITABLE");
        (await _fixture.ReadRawAttributeValueFromMongoDb(_fixture.ProbeUserRtId, "accessFailedCount", F.UserCollectionSuffix))
            .ToInt32().Should().Be(3);
    }

    [Fact]
    public async Task GenericUpdate_OfTheUser_WithUserTokens_IsRefused_AndNothingChanges()
    {
        var result = await GenericUpdateAsync(_fixture.ProbeUserRtId, "userTokens", TokenRecords());

        AssertRefused(result, "ATTRIBUTE_NOT_WRITABLE");
        await AssertProbeUserUnchangedAsync();
    }

    [Theory]
    [InlineData("passwordHash")]
    [InlineData("securityStamp")]
    [InlineData("userTokens")]
    public async Task ClearSecretAttributes_OnACredential_DoesNotTouchIt(string attribute)
    {
        var result = await _fixture.ExecuteGraphQlAsync(GenericUpdateMutation, JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    rtId = _fixture.ProbeUserRtId,
                    item = new { ckTypeId = F.UserCkTypeId, attributes = Array.Empty<object>() },
                    clearSecretAttributes = new[] { attribute }
                }
            }
        }));

        AssertNoDummyValue(_fixture.SerializeGraphQl(result));
        await AssertProbeUserUnchangedAsync();
    }

    [Theory]
    [InlineData("passwordHash", "Hidden")]
    [InlineData("securityStamp", "Hidden")]
    [InlineData("userTokens.value", "Hidden")]
    [InlineData("userName", "MethodOnly")]
    [InlineData("email", "MethodOnly")]
    public async Task QueryRowCreate_WithARestrictedCell_IsRefused(string cell, string access)
    {
        var queryRtId = await CreateUserQueryAsync();

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { runtimeQuery(rtId: "{{queryRtId}}") {
              create(entities: [{ ckTypeId: "{{F.UserCkTypeId}}", cells: [
                { attributePath: "firstName", value: "row-create" },
                { attributePath: "{{cell}}", value: "{{F.DummyPasswordHash}}" }
              ] }]) { ckTypeId }
            } } }
            """);

        AssertRefused(result, "ATTRIBUTE_NOT_WRITABLE");
        result.Errors!.Select(e => e.Message).Should().Contain(m => m.Contains($"access: {access}"));
    }

    [Theory]
    [InlineData("passwordHash")]
    [InlineData("securityStamp")]
    [InlineData("userName")]
    [InlineData("email")]
    public async Task QueryRowUpdate_WithARestrictedCell_IsRefused_AndNothingChanges(string cell)
    {
        var queryRtId = await CreateUserQueryAsync();

        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { runtimeQuery(rtId: "{{queryRtId}}") {
              update(entities: [{ rtId: "{{_fixture.ProbeUserRtId}}", item: { ckTypeId: "{{F.UserCkTypeId}}", cells: [
                { attributePath: "{{cell}}", value: "overwritten" }
              ] } }]) { ckTypeId }
            } } }
            """);

        AssertRefused(result, "ATTRIBUTE_NOT_WRITABLE");
        await AssertProbeUserUnchangedAsync();
    }

    // ------------------------------------------------------------------------------------------------- methods

    [Fact]
    public async Task TwoAliasedIdentityMethodCalls_AreRefusedAsOneRequest_AndNothingIsDispatched()
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{F.UserMutationField}} {
              a: resetPassword(rtId: "{{_fixture.ProbeUserRtId}}", input: { newPassword: "{{DummyNewPassword}}" }) { invocationId }
              b: resetPassword(rtId: "{{_fixture.ProbeUserRtId}}", input: { newPassword: "{{DummyNewPassword}}" }) { invocationId }
            } } }
            """, user: UserManager());

        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().ContainSingle(json).Which.Code.Should().Be("ONE_METHOD_CALL_PER_REQUEST");
        result.Data.Should().BeNull();
        _fixture.Dispatcher.Requests.Should().BeEmpty();
        json.Should().NotContain(DummyNewPassword);
        AssertNoLogContains(DummyNewPassword);
    }

    [Fact]
    public async Task IdentityMethodNextToAGenericUpdateOfTheUser_IsRefused_AndNothingRuns()
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation ($entities: [RtEntityUpdate!]!) { runtime {
              {{F.UserMutationField}} { setRoles(rtId: "{{_fixture.ProbeUserRtId}}", input: { roleNames: ["TenantManagement"] }) { invocationId } }
              runtimeEntities { update(entities: $entities) { rtId } }
            } }
            """, JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    rtId = _fixture.ProbeUserRtId,
                    item = new { ckTypeId = F.UserCkTypeId, attributes = new[] { new { attributeName = "firstName", value = "x" } } }
                }
            }
        }), user: UserManager());

        result.Errors.Should().ContainSingle(_fixture.SerializeGraphQl(result)).Which.Code
            .Should().Be("ONE_METHOD_CALL_PER_REQUEST");
        _fixture.Dispatcher.Requests.Should().BeEmpty();
        await AssertProbeUserUnchangedAsync();
    }

    [Fact]
    public async Task IdentityMethod_WithoutTheRole_IsForbidden_AndTheSensitiveParameterIsNowhere()
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{F.UserMutationField}} {
              resetPassword(rtId: "{{_fixture.ProbeUserRtId}}", input: { newPassword: "{{DummyNewPassword}}" }) { invocationId }
            } } }
            """, user: Principal("65f0c0ffee65f0c0ffee6599", []));

        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().ContainSingle(json).Which.Code.Should().Be("METHOD_FORBIDDEN");
        _fixture.Dispatcher.Requests.Should().BeEmpty();
        json.Should().NotContain(DummyNewPassword);
        AssertNoLogContains(DummyNewPassword);
    }

    [Fact]
    public async Task IdentityMethod_WithTheRole_IsDispatched_AndTheSensitiveParameterIsNotLogged()
    {
        var result = await _fixture.ExecuteGraphQlAsync($$"""
            mutation { runtime { {{F.UserMutationField}} {
              resetPassword(rtId: "{{_fixture.ProbeUserRtId}}", input: { newPassword: "{{DummyNewPassword}}" }) { invocationId }
            } } }
            """, user: UserManager());

        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        _fixture.Dispatcher.Requests.Should().ContainSingle()
            .Which.MethodId.Should().Contain("ResetPassword");
        json.Should().NotContain(DummyNewPassword);
        AssertNoLogContains(DummyNewPassword);
        AssertNoDummyValue(json);
    }

    // ------------------------------------------------------------------------------------------------- logs

    // Every refusal above also asserts that no log line written during the test carries a dummy value
    // (AssertNoDummyValueInTestLogs). The write of the identity store itself is the open gap below.
    [Fact(Skip = "AB#8048: the MongoDB command log (Debug, and the slow-query preview at Warning) prints Hidden attribute values of a write in clear text")]
    public async Task Gap_TheIdentityStoreWrite_LeavesNoCredentialInTheMongoCommandLog()
    {
        await _fixture.SeedUserAsync("probe-log-" + Guid.NewGuid().ToString("N")[..8], "probe-log@example.invalid");

        AssertNoDummyValueInTestLogs();
    }

    // ------------------------------------------------------------------------------------------------- open gaps

    // Generic writes that still reach identity data without the identity methods. Enable each test when the attribute or
    // association becomes MethodOnly / ReadOnly in System.Identity and the snapshot in testData/ck-models is refreshed.
    [Theory(Skip = "AB#8042: still ReadWrite in System.Identity 2.29.0")]
    [InlineData("normalizedUserName", "OTHER-USER")]
    [InlineData("normalizedEmail", "OTHER@EXAMPLE.INVALID")]
    [InlineData("emailConfirmed", true)]
    [InlineData("twoFactorEnabled", false)]
    [InlineData("lockoutEnabled", false)]
    [InlineData("resetPasswordOnLogin", false)]
    public async Task Gap_GenericUpdate_OfAnIdentityFlag_IsRefused(string attribute, object value)
    {
        var result = await GenericUpdateAsync(_fixture.ProbeUserRtId, attribute, value);

        AssertRefused(result, "ATTRIBUTE_NOT_WRITABLE");
    }

    [Theory(Skip = "AB#8042: claims and external logins are still ReadWrite in System.Identity 2.29.0")]
    [InlineData("claims")]
    [InlineData("userLogins")]
    public async Task Gap_GenericWrite_OfClaimsOrExternalLogins_IsRefused(string attribute)
    {
        var result = await GenericUpdateAsync(_fixture.ProbeUserRtId, attribute, Array.Empty<object>());

        AssertRefused(result, "ATTRIBUTE_NOT_WRITABLE");
    }

    // Group membership (Group.members / User.memberOfGroups) has the same gap; it needs a seeded group.
    [Fact(Skip = "AB#8042: the AssignedRole association is still writable through the generic mutations")]
    public async Task Gap_GenericWrite_OfARoleAssignment_IsRefused()
    {
        var result = await GenericUpdateAsync(_fixture.ProbeUserRtId, "assignedRoles",
            new object[] { new { target = new { rtId = _fixture.ProbeRoleRtId, ckTypeId = F.RoleCkTypeId } } });

        AssertRefused(result, "ATTRIBUTE_NOT_WRITABLE");
    }

    [Fact(Skip = "Pending: CreateUser does not set replacesGenericCreate in System.Identity 2.29.0 (needs the next major)")]
    public async Task Gap_GenericCreate_OfAUser_IsReplacedByCreateUser()
    {
        var result = await _fixture.ExecuteGraphQlAsync(GenericCreateMutation, JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new { ckTypeId = F.UserCkTypeId, attributes = new[] { new { attributeName = "firstName", value = "generic" } } }
            }
        }));

        AssertRefused(result, "GENERIC_CREATE_REPLACED_BY_METHOD");
    }

    // ------------------------------------------------------------------------------------------------- helpers

    private const string DummyNewPassword = "DUMMY-NEWPASSWORD-7781";

    /// <summary>The guess a stored query carries; it is the caller's own value and is written with the query entity.</summary>
    private const string StoredGuess = "GUESS-7781";

    private const string GenericCreateMutation = """
        mutation ($entities: [RtEntityInput!]!) { runtime { runtimeEntities { create(entities: $entities) { rtId } } } }
        """;

    private const string GenericUpdateMutation = """
        mutation ($entities: [RtEntityUpdate!]!) { runtime { runtimeEntities { update(entities: $entities) { rtId } } } }
        """;

    private static object[] TokenRecords() =>
    [
        new
        {
            ckRecordId = F.UserTokenCkRecordId,
            attributes = new object[]
            {
                new { attributeName = "loginProvider", value = "[AspNetUserStore]" },
                new { attributeName = "name", value = "AuthenticatorKey" },
                new { attributeName = "userId", value = "x" },
                new { attributeName = "value", value = F.DummyTokenValue }
            }
        }
    ];

    private static ClaimsPrincipal Principal(string sub, string[] roles)
    {
        var claims = new List<Claim> { new("sub", sub), new("scope", "octo_api openid") };
        claims.AddRange(roles.Select(r => new Claim("role", r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static ClaimsPrincipal UserManager() => Principal("65f0c0ffee65f0c0ffee6501", ["UserManagement"]);

    private static string SelectorQuery(string column) => $$"""
        query { runtime { transientQuery {
          simple(ckId: "{{F.RoleCkTypeId}}", columnPaths: ["name", "{{column}}"]) {
            items { rows { items { ... on RtSimpleQueryRow { cells { items { attributePath value } } } } } } } } } }
        """;

    private Task<ExecutionResult> GenericUpdateAsync(string rtId, string attribute, object value) =>
        _fixture.ExecuteGraphQlAsync(GenericUpdateMutation, JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    rtId,
                    item = new { ckTypeId = F.UserCkTypeId, attributes = new[] { new { attributeName = attribute, value } } }
                }
            }
        }));

    private async Task<string> CreateGenericAsync(object entity)
    {
        var result = await _fixture.ExecuteGraphQlAsync(GenericCreateMutation,
            JsonSerializer.Serialize(new { entities = new[] { entity } }));
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return JObject.Parse(json).SelectToken("data.runtime.runtimeEntities.create[0].rtId")!.Value<string>()!;
    }

    private Task<string> CreateUserQueryAsync() => CreateGenericAsync(new
    {
        ckTypeId = "System/SimpleRtQuery",
        attributes = new object[]
        {
            new { attributeName = "name", value = "credential-rows" },
            new { attributeName = "queryCkTypeId", value = F.UserCkTypeId },
            new { attributeName = "columns", value = new[] { "firstName" } }
        }
    });

    private async Task AssertProbeUserUnchangedAsync()
    {
        var rtId = _fixture.ProbeUserRtId;
        (await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "passwordHash", F.UserCollectionSuffix)).AsString
            .Should().Be(F.DummyPasswordHash);
        (await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "securityStamp", F.UserCollectionSuffix)).AsString
            .Should().Be(F.DummySecurityStamp);
        (await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "userName", F.UserCollectionSuffix)).AsString
            .Should().Be(F.ProbeUserName);
        (await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "email", F.UserCollectionSuffix)).AsString
            .Should().Be(F.ProbeUserEmail);
        (await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "userTokens", F.UserCollectionSuffix)).IsBsonArray
            .Should().BeTrue("the tokens are still stored");
        (await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "userTokens", F.UserCollectionSuffix)).AsBsonArray
            .Should().HaveCount(1);
    }

    private async Task AssertRefusedAsync(string query, string code) =>
        AssertRefused(await _fixture.ExecuteGraphQlAsync(query), code);

    private void AssertRefused(ExecutionResult result, string code)
    {
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().NotBeNullOrEmpty(json);
        result.Errors!.Select(e => e.Code).Should().Contain(code, json);
        AssertNoDummyValue(json);
        JObject.Parse(json).SelectTokens("$..items[*].rtId").Should().BeEmpty("a refused query returns no rows: " + json);
        AssertNoDummyValueInTestLogs();
    }

    private void AssertNoDummyValueInTestLogs()
    {
        _fixture.LogLinesWithDummyValuesSince(_logStart).Should().BeEmpty("no log line may carry a credential");
    }

    private static void AssertNoDummyValue(string text)
    {
        foreach (var dummy in F.DummyValues)
        {
            text.Should().NotContain(dummy);
        }
    }

    private void AssertNoLogContains(string value)
    {
        lock (_fixture.Logs)
        {
            _fixture.Logs.Select(l => l.Text).Should().NotContain(t => t.Contains(value, StringComparison.Ordinal));
        }
    }

    private async Task<List<string>> FieldNamesAsync(string typeName, string fieldsKey)
    {
        var json = await ExecuteOkAsync($$"""query { __type(name: "{{typeName}}") { {{fieldsKey}} { name } } }""");
        var fields = JObject.Parse(json).SelectTokens($"data.__type.{fieldsKey}[*].name").Select(t => t.Value<string>()!)
            .ToList();
        fields.Should().NotBeEmpty($"type {typeName} must exist");
        return fields;
    }

    private async Task<HashSet<string>> ScalarFieldNamesAsync(string typeName)
    {
        var json = await ExecuteOkAsync($$"""
            query { __type(name: "{{typeName}}") { fields { name type { kind ofType { kind } } } } }
            """);
        return JObject.Parse(json).SelectTokens("data.__type.fields[*]")
            .Where(f => f["type"]!["kind"]!.Value<string>() is "SCALAR" or "ENUM"
                        || (f["type"]!["kind"]!.Value<string>() == "NON_NULL"
                            && f["type"]!["ofType"]!["kind"]!.Value<string>() is "SCALAR" or "ENUM"))
            .Select(f => f["name"]!.Value<string>()!)
            .ToHashSet();
    }

    private async Task<string> ExecuteOkAsync(string query)
    {
        var result = await _fixture.ExecuteGraphQlAsync(query);
        var json = _fixture.SerializeGraphQl(result);
        result.Errors.Should().BeNullOrEmpty(json);
        return json;
    }
}
