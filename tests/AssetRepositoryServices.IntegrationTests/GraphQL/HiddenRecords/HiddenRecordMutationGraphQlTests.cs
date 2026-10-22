using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.HiddenRecords;

/// <summary>
///     AB#7213 (follow-up of AB#7126): through the generic runtime-entity mutations a caller who may write a record but
///     cannot see it gets exactly the answer of a record that does not exist (<c>ASSET1001</c>),
///     and the record stays untouched. Policies: P1 = Write + Delete on all records, P2 = Read on own records only.
///     The "missing" cases use a well-formed rtId that no record carries and are the reference for the "hidden" cases.
/// </summary>
[Collection(HiddenRecordCollection.Name)]
public class HiddenRecordMutationGraphQlTests
{
    private const string CkTypeId = HiddenRecordGraphQlTestFixture.CkTypeId;
    private const string Suffix = HiddenRecordGraphQlTestFixture.CollectionSuffix;

    private const string CreateMutation = """
        mutation ($entities: [RtEntityInput!]!) {
          runtime { runtimeEntities { create(entities: $entities) { rtId } } }
        }
        """;

    private const string UpdateMutation = """
        mutation ($entities: [RtEntityUpdate!]!) {
          runtime { runtimeEntities { update(entities: $entities) { rtId } } }
        }
        """;

    private const string DeleteMutation = """
        mutation ($entities: [RtEntityId!]!) {
          runtime { runtimeEntities { delete(entities: $entities) } }
        }
        """;

    private const int ArchivedRtState = 1;

    private readonly HiddenRecordGraphQlTestFixture _fixture;

    public HiddenRecordMutationGraphQlTests(HiddenRecordGraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    // Criterion 1: update of a hidden record answers like a missing one (user B holds P1 + P2).
    [Fact]
    public async Task Update_OfAHiddenRecord_AnswersLikeAMissingOne_AndLeavesItUnchanged()
    {
        var hidden = await CreateAsUserAAsync("Hidden_Update");

        var hiddenError = await SingleErrorAsync(UpdateMutation, UpdateVariables(hidden, "Hacked"),
            HiddenRecordGraphQlTestFixture.UserBWithOwnedRead);
        var missing = NewMissingRtId();
        var missingError = await SingleErrorAsync(UpdateMutation, UpdateVariables(missing, "Hacked"),
            HiddenRecordGraphQlTestFixture.UserBWithOwnedRead);

        AssertSameNotFound(hiddenError, hidden, missingError, missing);
        (await FirstNameAsync(hidden)).Should().Be("Original");
    }

    // Criterion 2: the same for delete.
    [Fact]
    public async Task Delete_OfAHiddenRecord_AnswersLikeAMissingOne_AndKeepsIt()
    {
        var hidden = await CreateAsUserAAsync("Hidden_Delete");

        var hiddenError = await SingleErrorAsync(DeleteMutation, DeleteVariables(hidden),
            HiddenRecordGraphQlTestFixture.UserBWithOwnedRead);
        var missing = NewMissingRtId();
        var missingError = await SingleErrorAsync(DeleteMutation, DeleteVariables(missing),
            HiddenRecordGraphQlTestFixture.UserBWithOwnedRead);

        AssertSameNotFound(hiddenError, hidden, missingError, missing);
        (await _fixture.ReadRtStateAsync(hidden)).Should().NotBe(ArchivedRtState, "the hidden record is not deleted");
    }

    // Criterion 3: a record the caller owns stays updatable and deletable.
    [Fact]
    public async Task OwnRecord_CanBeUpdatedAndDeleted()
    {
        var own = await CreateAsync("Own", HiddenRecordGraphQlTestFixture.UserBWithOwnedRead);

        var update = await _fixture.ExecuteGraphQlAsync(UpdateMutation, UpdateVariables(own, "Edited"),
            HiddenRecordGraphQlTestFixture.UserBWithOwnedRead);
        update.Errors.Should().BeNullOrEmpty();
        (await FirstNameAsync(own)).Should().Be("Edited");

        var delete = await _fixture.ExecuteGraphQlAsync(DeleteMutation, DeleteVariables(own),
            HiddenRecordGraphQlTestFixture.UserBWithOwnedRead);
        delete.Errors.Should().BeNullOrEmpty();
        (await _fixture.ReadRtStateAsync(own)).Should().Be(ArchivedRtState, "the default delete strategy archives");
    }

    // Criterion 4: no Read grant at all - insert works, update/delete of an existing record are "not found".
    [Fact]
    public async Task WithoutAnyReadGrant_InsertSucceeds_ButUpdateAndDeleteOfExistingRecordAreNotFound()
    {
        var existing = await CreateAsUserAAsync("NoRead_Existing");

        // Insert by the caller without Read: allowed (Write covers insert; the result carries no readable data).
        var insert = await _fixture.ExecuteGraphQlAsync(CreateMutation, CreateVariables("NoRead_Insert"),
            HiddenRecordGraphQlTestFixture.UserBWithoutRead);
        insert.Errors.Should().BeNullOrEmpty();

        var missing = NewMissingRtId();
        var updateError = await SingleErrorAsync(UpdateMutation, UpdateVariables(existing, "Hacked"),
            HiddenRecordGraphQlTestFixture.UserBWithoutRead);
        var updateMissing = await SingleErrorAsync(UpdateMutation, UpdateVariables(missing, "Hacked"),
            HiddenRecordGraphQlTestFixture.UserBWithoutRead);
        AssertSameNotFound(updateError, existing, updateMissing, missing);

        var deleteError = await SingleErrorAsync(DeleteMutation, DeleteVariables(existing),
            HiddenRecordGraphQlTestFixture.UserBWithoutRead);
        var deleteMissing = await SingleErrorAsync(DeleteMutation, DeleteVariables(missing),
            HiddenRecordGraphQlTestFixture.UserBWithoutRead);
        AssertSameNotFound(deleteError, existing, deleteMissing, missing);

        (await FirstNameAsync(existing)).Should().Be("Original");
        (await _fixture.ReadRtStateAsync(existing)).Should().NotBe(ArchivedRtState, "the record is not deleted");
    }

    private static string NewMissingRtId() => MongoDB.Bson.ObjectId.GenerateNewId().ToString();

    private Task<string> CreateAsUserAAsync(string name) => CreateAsync(name, HiddenRecordGraphQlTestFixture.UserA);

    private async Task<string> CreateAsync(string name, ClaimsPrincipal user)
    {
        var result = await _fixture.ExecuteGraphQlAsync(CreateMutation, CreateVariables(name), user);
        result.Errors.Should().BeNullOrEmpty();
        return JObject.Parse(_fixture.SerializeGraphQl(result))
            .SelectToken("data.runtime.runtimeEntities.create[0].rtId")!.Value<string>()!;
    }

    private async Task<string?> FirstNameAsync(string rtId)
    {
        return (await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "firstName", Suffix)).AsString;
    }

    private async Task<JToken> SingleErrorAsync(string mutation, string variables, ClaimsPrincipal user)
    {
        var result = await _fixture.ExecuteGraphQlAsync(mutation, variables, user);
        result.Errors.Should().NotBeNullOrEmpty();
        var errors = (JArray)JObject.Parse(_fixture.SerializeGraphQl(result))["errors"]!;
        return errors.Should().ContainSingle().Subject;
    }

    /// <summary>
    ///     Code, message number and the whole message must be identical once the rtId is substituted; the data of the
    ///     response is empty in both cases.
    /// </summary>
    private static void AssertSameNotFound(JToken hidden, string hiddenRtId, JToken missing, string missingRtId)
    {
        missing.SelectToken("extensions.code")!.Value<string>().Should().Be("ASSET1001");
        hidden.SelectToken("extensions.code")!.Value<string>().Should().Be("ASSET1001");
        hidden.SelectToken("extensions.messageNumber")?.ToString()
            .Should().Be(missing.SelectToken("extensions.messageNumber")?.ToString());
        hidden["message"]!.Value<string>()!.Replace(hiddenRtId, "<rtId>", StringComparison.Ordinal)
            .Should().Be(missing["message"]!.Value<string>()!.Replace(missingRtId, "<rtId>", StringComparison.Ordinal));
        Normalise(hidden, hiddenRtId).Should().Be(Normalise(missing, missingRtId),
            "a hidden record must not be distinguishable from a missing one");
    }

    private static string Normalise(JToken error, string rtId)
    {
        return error.ToString(Newtonsoft.Json.Formatting.None).Replace(rtId, "<rtId>", StringComparison.Ordinal);
    }

    private static string CreateVariables(string name) => JsonSerializer.Serialize(new
    {
        entities = new[]
        {
            new
            {
                ckTypeId = CkTypeId,
                rtWellKnownName = $"HiddenRecord_{name}_{Guid.NewGuid():N}",
                attributes = new[]
                {
                    new { attributeName = "firstName", value = "Original" },
                    new { attributeName = "lastName", value = "Customer" },
                    new { attributeName = "street", value = "Test Street 1" },
                    new { attributeName = "postalCode", value = "12345" },
                    new { attributeName = "city", value = "Test City" },
                    new { attributeName = "country", value = "Austria" }
                }
            }
        }
    });

    private static string UpdateVariables(string rtId, string firstName) => JsonSerializer.Serialize(new
    {
        entities = new[]
        {
            new
            {
                rtId,
                item = new
                {
                    ckTypeId = CkTypeId,
                    attributes = new[] { new { attributeName = "firstName", value = firstName } }
                }
            }
        }
    });

    private static string DeleteVariables(string rtId) =>
        JsonSerializer.Serialize(new { entities = new[] { new { rtId, ckTypeId = CkTypeId } } });
}
