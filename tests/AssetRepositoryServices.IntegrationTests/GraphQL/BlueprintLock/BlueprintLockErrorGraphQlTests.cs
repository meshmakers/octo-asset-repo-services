using System.Text.Json;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.BlueprintLock;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.GraphQL.BlueprintLock;

/// <summary>
///     AB#6385: against a real data policy (<c>ProtectBlueprintLocked</c>, Enforce) a tenant user who may write the type
///     still cannot change or delete a blueprint-locked entity. The GraphQL response carries the stable error
///     <c>BLUEPRINT_LOCKED</c> with message number 6384, ckTypeId, rtId and reason; the change set is rejected as a whole
///     and nothing is written. Unlocked entities stay editable, a system session may write locked entities.
/// </summary>
[Collection(BlueprintLockCollection.Name)]
public class BlueprintLockErrorGraphQlTests
{
    private const string CkTypeId = BlueprintLockGraphQlTestFixture.ProtectedCkTypeId;
    private const string Suffix = BlueprintLockGraphQlTestFixture.CustomerCollectionSuffix;

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

    private readonly BlueprintLockGraphQlTestFixture _fixture;

    public BlueprintLockErrorGraphQlTests(BlueprintLockGraphQlTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    [Fact]
    public async Task Update_LockedEntity_ReturnsStableErrorAndLeavesEntityUnchanged()
    {
        var rtId = await CreateCustomerAsync("Locked_Update", locked: true);

        var errors = await ExecuteForErrorsAsync(UpdateMutation, UpdateVariables((rtId, "Hacked")));

        var error = AssertBlueprintLocked(errors, rtId, "EntityLocked");
        error.SelectToken("message")!.Value<string>().Should().Contain("locked by blueprint");
        (await FirstNameAsync(rtId)).Should().Be("Original");
    }

    [Fact]
    public async Task Delete_LockedEntity_ReturnsStableErrorAndKeepsEntity()
    {
        var rtId = await CreateCustomerAsync("Locked_Delete", locked: true);

        var errors = await ExecuteForErrorsAsync(DeleteMutation,
            JsonSerializer.Serialize(new { entities = new[] { new { rtId, ckTypeId = CkTypeId } } }));

        AssertBlueprintLocked(errors, rtId, "EntityLocked");
        (await FirstNameAsync(rtId)).Should().Be("Original");
    }

    [Fact]
    public async Task Batch_WithOneLockedEntity_IsRejectedAtomically()
    {
        var unlocked = await CreateCustomerAsync("Batch_Unlocked", locked: false);
        var locked = await CreateCustomerAsync("Batch_Locked", locked: true);

        var errors = await ExecuteForErrorsAsync(UpdateMutation,
            UpdateVariables((unlocked, "ChangedInBatch"), (locked, "ChangedInBatch")));

        var error = AssertBlueprintLocked(errors, locked, "EntityLocked");
        error.SelectToken("extensions.items")!.Select(i => i["rtId"]!.Value<string>()).Should().Equal(locked);
        (await FirstNameAsync(unlocked)).Should().Be("Original", "the unlocked entity of the rejected batch is not written");
        (await FirstNameAsync(locked)).Should().Be("Original");
    }

    [Fact]
    public async Task Batch_WithSeveralLockedEntities_ListsEveryOne()
    {
        var first = await CreateCustomerAsync("Batch_Locked_1", locked: true);
        var second = await CreateCustomerAsync("Batch_Locked_2", locked: true);

        var errors = await ExecuteForErrorsAsync(UpdateMutation, UpdateVariables((first, "X"), (second, "X")));

        var error = AssertBlueprintLocked(errors, first, "EntityLocked");
        error.SelectToken("extensions.items")!.Select(i => i["rtId"]!.Value<string>()).Should().BeEquivalentTo(first, second);
    }

    [Fact]
    public async Task UnlockedEntity_CanBeUpdatedAndDeleted()
    {
        var rtId = await CreateCustomerAsync("Unlocked", locked: false);

        var update = await _fixture.ExecuteGraphQlAsync(UpdateMutation, UpdateVariables((rtId, "Edited")),
            BlueprintLockGraphQlTestFixture.Editor);
        update.Errors.Should().BeNullOrEmpty();
        (await FirstNameAsync(rtId)).Should().Be("Edited");

        var delete = await _fixture.ExecuteGraphQlAsync(DeleteMutation,
            JsonSerializer.Serialize(new { entities = new[] { new { rtId, ckTypeId = CkTypeId } } }),
            BlueprintLockGraphQlTestFixture.Editor);
        delete.Errors.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task SystemSession_CanWriteLockedEntity()
    {
        var rtId = await CreateCustomerAsync("Locked_System", locked: true);
        var repository = _fixture.GetSystemContext().GetSystemTenantRepository();

        // A parameterless repository session is the system context (blueprint apply, internal services):
        // the guard lets it write locked entities.
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var entityId = new RtEntityId(new RtCkId<CkTypeId>(CkTypeId), new OctoObjectId(rtId));
        var entity = await repository.GetRtEntityByRtIdAsync(session, entityId);
        entity!.RtWellKnownName = "RenamedBySystem" + Guid.NewGuid().ToString("N");
        var result = new OperationResult();
        await repository.ApplyChangesAsync(session, [EntityUpdateInfo<RtEntity>.CreateUpdate(entityId, entity)], [],
            result);
        await session.CommitTransactionAsync();

        result.HasErrors.Should().BeFalse();
    }

    [Fact]
    public async Task Rest_FilterMapsTheRealEngineRefusalToProblemDetails()
    {
        var rtId = await CreateCustomerAsync("Locked_Rest", locked: true);
        var repository = _fixture.GetSystemContext().GetTenantRepository();

        // The refusal as every non-GraphQL caller sees it: a user session, a real change set, the real engine message.
        using var session = repository.GetSession(RtSecurityContext.ForUser("user-editor",
            [BlueprintLockGraphQlTestFixture.EditorRole]));
        session.StartTransaction();
        var entityId = new RtEntityId(new RtCkId<CkTypeId>(CkTypeId), new OctoObjectId(rtId));
        var entity = new RtEntity { RtWellKnownName = "Renamed" + Guid.NewGuid().ToString("N") };
        var result = new OperationResult();
        var apply = () => repository.ApplyChangesAsync(session,
            [EntityUpdateInfo<RtEntity>.CreateUpdate(entityId, entity)], [], result);
        var exception = (await apply.Should().ThrowAsync<RuntimeRepositoryException>()).Which;
        await session.AbortTransactionAsync();

        var context = new ExceptionContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), [])
        {
            Exception = exception
        };
        new BlueprintLockedExceptionFilter().OnException(context);

        var objectResult = context.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions["code"].Should().Be("BLUEPRINT_LOCKED");
        problem.Extensions["messageNumber"].Should().Be(6384);
        problem.Extensions["rtId"].Should().Be(rtId);
    }

    private async Task<string> CreateCustomerAsync(string name, bool locked)
    {
        var variables = JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new
                {
                    ckTypeId = CkTypeId,
                    rtWellKnownName = $"BlueprintLock_{name}_{Guid.NewGuid():N}",
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

        var result = await _fixture.ExecuteGraphQlAsync(CreateMutation, variables,
            BlueprintLockGraphQlTestFixture.Editor);
        result.Errors.Should().BeNullOrEmpty();
        var rtId = JObject.Parse(_fixture.SerializeGraphQl(result))
            .SelectToken("data.runtime.runtimeEntities.create[0].rtId")!.Value<string>()!;

        if (locked)
        {
            // What a blueprint apply leaves behind: the stored entity carries RtBlueprintLocked = true.
            await _fixture.SetRawAttributeValueInMongoDb(rtId, "rtBlueprintLocked", BsonBoolean.True, Suffix);
        }

        return rtId;
    }

    private async Task<string?> FirstNameAsync(string rtId)
    {
        return (await _fixture.ReadRawAttributeValueFromMongoDb(rtId, "firstName", Suffix)).AsString;
    }

    private static string UpdateVariables(params (string RtId, string FirstName)[] items)
    {
        return JsonSerializer.Serialize(new
        {
            entities = items.Select(i => new
            {
                rtId = i.RtId,
                item = new
                {
                    ckTypeId = CkTypeId,
                    attributes = new[] { new { attributeName = "firstName", value = i.FirstName } }
                }
            })
        });
    }

    private async Task<JArray> ExecuteForErrorsAsync(string mutation, string variables)
    {
        var result = await _fixture.ExecuteGraphQlAsync(mutation, variables, BlueprintLockGraphQlTestFixture.Editor);
        result.Errors.Should().NotBeNullOrEmpty();
        return (JArray)JObject.Parse(_fixture.SerializeGraphQl(result))["errors"]!;
    }

    private static JToken AssertBlueprintLocked(JArray errors, string rtId, string reason)
    {
        var error = errors.Should().ContainSingle().Subject;
        error.SelectToken("extensions.code")!.Value<string>().Should().Be("BLUEPRINT_LOCKED");
        error.SelectToken("extensions.messageNumber")!.Value<int>().Should().Be(6384);
        error.SelectToken("extensions.rtId")!.Value<string>().Should().Be(rtId);
        error.SelectToken("extensions.ckTypeId")!.Value<string>().Should().Contain("Customer");
        error.SelectToken("extensions.reason")!.Value<string>().Should().Be(reason);
        return error;
    }
}
