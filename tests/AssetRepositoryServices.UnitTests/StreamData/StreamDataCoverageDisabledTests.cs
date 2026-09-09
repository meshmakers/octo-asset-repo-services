using System.Collections;
using FakeItEasy;
using FluentAssertions;
using GraphQL;
using GraphQL.Execution;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AssetRepositoryServices.UnitTests.StreamData;

/// <summary>
///     AB#5157 — coverage is reported for a tenant that has StreamData switched off as an EMPTY family,
///     never as an error: <c>ITenantContext.GetArchiveFamilyCoverageService()</c> returns null and both
///     surfaces answer with an empty list. The REST endpoint must stay a 200 (the studio renders "no
///     coverage" rather than an error toast) and the <c>coverageFor</c> GraphQL resolver must return an
///     empty list instead of throwing a null reference.
/// </summary>
public class StreamDataCoverageDisabledTests
{
    private const string TenantId = "maco";

    private readonly ITenantContext _tenantContext = A.Fake<ITenantContext>();

    public StreamDataCoverageDisabledTests()
    {
        // A fake returns a dummy for an interface-typed member, so the disabled state has to be stated.
        A.CallTo(() => _tenantContext.GetArchiveFamilyCoverageService()).Returns(null);
    }

    [Fact]
    public async Task GetArchiveCoverage_ReturnsOkWithAnEmptyList_WhenStreamDataIsDisabled()
    {
        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.FindTenantContextAsync(TenantId)).Returns(_tenantContext);
        var controller = new StreamDataController(
            A.Fake<ILogger<StreamDataController>>(),
            systemContext,
            A.Fake<IHostApplicationLifetime>());

        var result = await controller.GetArchiveCoverage(TenantId, OctoObjectId.GenerateNewId().ToString());

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<IEnumerable<ArchiveCoverageRestDto>>()
            .Which.Should().BeEmpty();
    }

    [Fact]
    public async Task CoverageFor_ResolvesToAnEmptyList_WhenStreamDataIsDisabled()
    {
        var query = new StreamDataQuery(A.Fake<ILogger<StreamDataQuery>>());
        var resolver = query.GetField("coverageFor")!.Resolver!;
        var context = new ResolveFieldContext<object?>
        {
            Arguments = new Dictionary<string, ArgumentValue>
            {
                [Statics.RtIdArg] = new(OctoObjectId.GenerateNewId(), ArgumentSource.Literal),
            },
            UserContext = new GraphQlUserContext(null, _tenantContext),
        };

        var result = await resolver.ResolveAsync(context);

        result.Should().BeAssignableTo<IEnumerable>();
        ((IEnumerable)result!).Cast<object>().Should().BeEmpty();
    }
}
