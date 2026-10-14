using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Meta;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.UnitTests.Services;

/// <summary>
/// AB#6674: the reason a CK model is ResolveFailed reaches LibraryStatus (REST) and the GraphQL CkModel DTO, and only
/// while the model is ResolveFailed.
/// </summary>
public class CkModelLibraryStatusResolveFailedReasonTests
{
    private const string TenantId = "test-tenant";
    private const string Reason = "Unmet dependency: Base-[1.0,2.0) (floor 1.1.0): installed Base-1.0.0";

    private static CkModelLibraryStatusService CreateSut(params CkModel[] installed)
    {
        var resultSet = A.Fake<IResultSet<CkModel>>();
        A.CallTo(() => resultSet.Items).Returns(installed);
        var repository = A.Fake<ITenantRepository>();
        A.CallTo(() => repository.GetCkModelsAsync(A<IOctoSession>._, A<List<CkModelId>?>._, A<RtEntityQueryOptions>._,
                A<int?>._, A<int?>._))
            .Returns(resultSet);
        var tenantContext = A.Fake<ITenantContext>();
        A.CallTo(() => tenantContext.GetTenantRepository()).Returns(repository);
        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.FindTenantContextAsync(TenantId)).Returns(tenantContext);
        var catalog = A.Fake<ICatalogService>();
        A.CallTo(() => catalog.ListAsync(A<int>._, A<int>._, A<object?>._, A<CancellationToken?>._))
            .Returns(new ModelListResult { TotalCount = 0, SkippedCount = 0, TakeCount = 500, ModelResultItems = [] });
        return new CkModelLibraryStatusService(catalog, systemContext);
    }

    [Fact]
    public async Task LibraryStatus_ReturnsTheReasonOnlyForResolveFailedModels()
    {
        var sut = CreateSut(
            new CkModel
            {
                Id = new CkModelId("Dep-1.0.0"), ModelId = "Dep", ModelState = ModelState.ResolveFailed,
                ResolveFailedReason = Reason
            },
            // A stale value on a healthy row must never be shown.
            new CkModel
            {
                Id = new CkModelId("Healthy-1.0.0"), ModelId = "Healthy", ModelState = ModelState.Available,
                ResolveFailedReason = Reason
            });

        var status = await sut.GetLibraryStatusAsync(TenantId, TestContext.Current.CancellationToken);

        status.Items.Single(i => i.Name == "Dep").ResolveFailedReason.Should().Be(Reason);
        status.Items.Single(i => i.Name == "Healthy").ResolveFailedReason.Should().BeNull();
    }

    [Theory]
    [InlineData(ModelState.ResolveFailed, true)]
    [InlineData(ModelState.Available, false)]
    public void GraphQlDto_CarriesTheReasonOnlyWhileResolveFailed(ModelState state, bool expected)
    {
        var dto = (CkV2CkModelDto)CkModelDtoType.CreateCkModelDto(new CkModel
        {
            Id = new CkModelId("Dep-1.0.0"), ModelId = "Dep", ModelState = state, ResolveFailedReason = Reason
        });

        dto.ResolveFailedReason.Should().Be(expected ? Reason : null);
    }
}
