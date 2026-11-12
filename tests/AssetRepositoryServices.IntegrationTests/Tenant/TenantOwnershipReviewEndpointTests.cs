using System.Security.Claims;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Tenant;

/// <summary>
///     AB#8796: <c>POST system/v1/tenants/review-ownership</c> against a real MongoDB with the engine's review. A
///     tenant database without ownership marker is listed without a write, stamped by <c>stamp=true</c>, and a second
///     stamp changes nothing; callers outside the system tenant are forbidden.
/// </summary>
[Collection(AssetRepoCollection.Name)]
public class TenantOwnershipReviewEndpointTests(AssetRepoFixture fixture)
{
    private const string OwnershipCollection = "tenant_ownership";

    private ISystemContext SystemContext => fixture.GetSystemContext();

    private TenantRegistryController CreateController(ClaimsPrincipal user) =>
        new(SystemContext, NullLogger<TenantRegistryController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } }
        };

    private ClaimsPrincipal SystemAdmin() => new(new ClaimsIdentity(
    [
        new Claim("sub", "system-admin"),
        new Claim("tenant_id", SystemContext.TenantId),
        new Claim(ClaimTypes.Role, CommonConstants.AdminPanelManagementRole)
    ], "IntegrationTests"));

    private IMongoCollection<BsonDocument> Marker(string databaseName) =>
        new MongoClient(fixture.GetConnectionString()).GetDatabase(databaseName)
            .GetCollection<BsonDocument>(OwnershipCollection);

    private static TenantOwnershipReviewReportDto Report(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should()
            .BeOfType<TenantOwnershipReviewReportDto>().Subject;

    [Fact]
    public async Task Review_ListsWithoutWriting_StampWritesTheMarker_SecondStampChangesNothing_AndOthersGetForbidden()
    {
        var tenantId = $"own-rev-{Guid.NewGuid():N}"[..20];
        using (var session = await SystemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await SystemContext.CreateChildTenantAsync(session, tenantId, tenantId);
            await session.CommitTransactionAsync();
        }

        try
        {
            // A tenant database from before the ownership marker existed.
            await Marker(tenantId).DeleteManyAsync(FilterDefinition<BsonDocument>.Empty,
                TestContext.Current.CancellationToken);

            var controller = CreateController(SystemAdmin());

            var listed = Report(await controller.ReviewOwnership());
            listed.Stamp.Should().BeFalse();
            listed.Entries.Should().ContainSingle(e => e.TenantId == tenantId && e.Issue == "Unmarked" && !e.Stamped);
            (await Marker(tenantId).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty,
                cancellationToken: TestContext.Current.CancellationToken)).Should().Be(0, "listing writes nothing");

            var stamped = Report(await controller.ReviewOwnership(stamp: true));
            stamped.Entries.Should().ContainSingle(e => e.TenantId == tenantId && e.Stamped);
            var marker = await Marker(tenantId).Find(FilterDefinition<BsonDocument>.Empty)
                .SingleAsync(TestContext.Current.CancellationToken);
            marker["ownerSystemDatabaseName"].AsString.Should().Be(stamped.SystemDatabaseName);

            var again = Report(await controller.ReviewOwnership(stamp: true));
            again.Entries.Should().NotContain(e => e.TenantId == tenantId);

            var other = CreateController(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "x"), new Claim("tenant_id", "customer"),
                 new Claim(ClaimTypes.Role, CommonConstants.AdminPanelManagementRole)], "IntegrationTests")));
            (await other.ReviewOwnership(stamp: true)).Should().BeOfType<ForbidResult>();
        }
        finally
        {
            try
            {
                using var session = await SystemContext.GetAdminSessionAsync();
                session.StartTransaction();
                await SystemContext.DropChildTenantAsync(session, tenantId);
                await session.CommitTransactionAsync();
            }
            catch (Exception)
            {
                // cleanup only
            }
        }
    }
}
