using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AssetRepositoryServices.UnitTests.TenantApi;

public class TenantsControllerTests
{
    private const string OwnTenantId = "maco";
    private const string OwnDatabase = "maco_db";

    private readonly IOctoService _octoService;
    private readonly ISystemContext _systemContext;
    private readonly ITenantContext _tenantContext;
    private readonly TenantsController _controller;

    public TenantsControllerTests()
    {
        _octoService = A.Fake<IOctoService>();
        _systemContext = A.Fake<ISystemContext>();
        _tenantContext = A.Fake<ITenantContext>();

        A.CallTo(() => _octoService.SystemContext).Returns(_systemContext);
        A.CallTo(() => _systemContext.TryFindTenantContextAsync(OwnTenantId)).Returns(_tenantContext);
        A.CallTo(() => _tenantContext.TenantId).Returns(OwnTenantId);
        A.CallTo(() => _tenantContext.DatabaseName).Returns(OwnDatabase);
        A.CallTo(() => _tenantContext.GetAdminSessionAsync()).Returns(A.Fake<IOctoAdminSession>());

        _controller = new TenantsController(
            _octoService,
            A.Fake<IDistributionEventHubService>(),
            A.Fake<ITenantLifecycleStore>());

        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = OwnTenantId;
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    private void SetupChildTenants(int totalCount, params OctoTenant[] items)
    {
        var resultSet = A.Fake<IResultSet<OctoTenant>>();
        A.CallTo(() => resultSet.Items).Returns(items);
        A.CallTo(() => resultSet.TotalCount).Returns(totalCount);
        A.CallTo(() => _tenantContext.GetChildTenantsAsync(A<IOctoAdminSession>._, A<int?>._, A<int?>._))
            .Returns(resultSet);
    }

    [Fact]
    public async Task Get_IncludesOwnTenant_WhenTenantHasNoChildren()
    {
        SetupChildTenants(0);

        var result = await _controller.Get(new PagingParams { Skip = 0, Take = 100 });

        var paged = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<PagedResult<TenantDto>>().Subject;
        paged.List.Should().ContainSingle()
            .Which.Should().Match<TenantDto>(t => t.TenantId == OwnTenantId && t.Database == OwnDatabase);
        paged.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task Get_IncludesOwnTenantFirst_WithChildren()
    {
        SetupChildTenants(2,
            new OctoTenant("child-a", "child-a-db"),
            new OctoTenant("child-b", "child-b-db"));

        var result = await _controller.Get(new PagingParams { Skip = 0, Take = 100 });

        var paged = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<PagedResult<TenantDto>>().Subject;
        paged.List.Should().HaveCount(3);
        paged.List.First().TenantId.Should().Be(OwnTenantId);
        paged.List.Select(t => t.TenantId).Should().Contain(new[] { "child-a", "child-b" });
        paged.TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task Get_DoesNotDuplicateOwnTenant_OnSubsequentPages()
    {
        SetupChildTenants(3,
            new OctoTenant("child-b", "child-b-db"),
            new OctoTenant("child-c", "child-c-db"));

        var result = await _controller.Get(new PagingParams { Skip = 2, Take = 2 });

        var paged = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<PagedResult<TenantDto>>().Subject;
        paged.List.Select(t => t.TenantId).Should().NotContain(OwnTenantId);
        paged.TotalCount.Should().Be(4);
        A.CallTo(() => _tenantContext.GetChildTenantsAsync(A<IOctoAdminSession>._, 1, 2))
            .MustHaveHappened();
    }

    [Fact]
    public async Task Get_ReturnsOnlyOwnTenant_OnFirstPageOfSizeOne()
    {
        SetupChildTenants(2, new OctoTenant("child-a", "child-a-db"));

        var result = await _controller.Get(new PagingParams { Skip = 0, Take = 1 });

        var paged = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<PagedResult<TenantDto>>().Subject;
        paged.List.Should().ContainSingle().Which.TenantId.Should().Be(OwnTenantId);
        paged.TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task Get_ReturnsOwnTenantAndAllChildren_WhenNotPaged()
    {
        SetupChildTenants(2,
            new OctoTenant("child-a", "child-a-db"),
            new OctoTenant("child-b", "child-b-db"));

        var result = await _controller.Get((PagingParams?)null);

        var list = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<IEnumerable<TenantDto>>().Subject.ToList();
        list.Should().HaveCount(3);
        list[0].TenantId.Should().Be(OwnTenantId);
    }

    [Fact]
    public async Task Get_TenChildren_FirstPage_ShowsOwnPlusNineChildren()
    {
        SetupPagedChildren(Children(10));

        var paged = await GetPaged(new PagingParams { Skip = 0, Take = 10 });

        paged.List.Should().HaveCount(10);
        paged.List.First().TenantId.Should().Be(OwnTenantId);
        paged.List.Skip(1).Select(t => t.TenantId).Should().Equal(ChildIds(0, 9));
        paged.TotalCount.Should().Be(11);
    }

    [Fact]
    public async Task Get_TenChildren_SecondPage_ShowsLastChildOnly()
    {
        SetupPagedChildren(Children(10));

        var paged = await GetPaged(new PagingParams { Skip = 10, Take = 10 });

        paged.List.Select(t => t.TenantId).Should().Equal("child-9");
        paged.List.Select(t => t.TenantId).Should().NotContain(OwnTenantId);
        paged.TotalCount.Should().Be(11);
    }

    [Fact]
    public async Task Get_ElevenChildren_FirstPage_ShowsOwnPlusNineChildren()
    {
        SetupPagedChildren(Children(11));

        var paged = await GetPaged(new PagingParams { Skip = 0, Take = 10 });

        paged.List.Should().HaveCount(10);
        paged.List.First().TenantId.Should().Be(OwnTenantId);
        paged.List.Skip(1).Select(t => t.TenantId).Should().Equal(ChildIds(0, 9));
        paged.TotalCount.Should().Be(12);
    }

    [Fact]
    public async Task Get_ElevenChildren_SecondPage_ShowsTwoRemainingChildren()
    {
        SetupPagedChildren(Children(11));

        var paged = await GetPaged(new PagingParams { Skip = 10, Take = 10 });

        paged.List.Select(t => t.TenantId).Should().Equal("child-9", "child-10");
        paged.List.Select(t => t.TenantId).Should().NotContain(OwnTenantId);
        paged.TotalCount.Should().Be(12);
    }

    [Fact]
    public async Task Get_ConcatenatedPages_CoverEveryRowOnceWithoutGaps()
    {
        SetupPagedChildren(Children(11));

        var page1 = await GetPaged(new PagingParams { Skip = 0, Take = 10 });
        var page2 = await GetPaged(new PagingParams { Skip = 10, Take = 10 });

        var seen = page1.List.Concat(page2.List).Select(t => t.TenantId).ToList();
        seen.Should().OnlyHaveUniqueItems();
        seen.Should().Equal(new[] { OwnTenantId }.Concat(ChildIds(0, 11)));
    }

    [Fact]
    public async Task Get_SkipBeyondEnd_ReturnsEmptyPage()
    {
        SetupPagedChildren(Children(2));

        var paged = await GetPaged(new PagingParams { Skip = 10, Take = 10 });

        paged.List.Should().BeEmpty();
        paged.TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task Get_SecondPageOfSizeOne_ShowsFirstChildNotOwn()
    {
        SetupPagedChildren(Children(3));

        var paged = await GetPaged(new PagingParams { Skip = 1, Take = 1 });

        paged.List.Select(t => t.TenantId).Should().Equal("child-0");
        paged.TotalCount.Should().Be(4);
    }

    [Fact]
    public async Task Get_LargeTake_ReturnsOwnPlusAllChildrenOnOnePage()
    {
        SetupPagedChildren(Children(3));

        var paged = await GetPaged(new PagingParams { Skip = 0, Take = 100 });

        paged.List.Select(t => t.TenantId).Should().Equal(new[] { OwnTenantId }.Concat(ChildIds(0, 3)));
        paged.TotalCount.Should().Be(4);
    }

    private async Task<PagedResult<TenantDto>> GetPaged(PagingParams pagingParams)
    {
        var result = await _controller.Get(pagingParams);
        return result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<PagedResult<TenantDto>>().Subject;
    }

    private static OctoTenant[] Children(int count) =>
        Enumerable.Range(0, count).Select(i => new OctoTenant($"child-{i}", $"child-{i}-db")).ToArray();

    private static IEnumerable<string> ChildIds(int startInclusive, int endExclusive) =>
        Enumerable.Range(startInclusive, endExclusive - startInclusive).Select(i => $"child-{i}");

    private void SetupPagedChildren(OctoTenant[] all)
    {
        A.CallTo(() => _tenantContext.GetChildTenantsAsync(A<IOctoAdminSession>._, A<int?>._, A<int?>._))
            .ReturnsLazily((IOctoAdminSession _, int? skip, int? take) =>
            {
                IEnumerable<OctoTenant> slice = all.Skip(skip ?? 0);
                if (take.HasValue)
                {
                    slice = slice.Take(take.Value);
                }

                var resultSet = A.Fake<IResultSet<OctoTenant>>();
                A.CallTo(() => resultSet.Items).Returns(slice.ToArray());
                A.CallTo(() => resultSet.TotalCount).Returns((long)all.Length);
                return resultSet;
            });
    }
}
