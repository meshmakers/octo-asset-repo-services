using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Observability;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Xunit;

namespace AssetRepositoryServices.UnitTests.Observability;

/// <summary>
///     AB#5432: observability is opt-in, so the one behaviour that must never drift is what happens
///     when the answer is not a clean "true". Absent entity, absent attribute, unknown tenant — all
///     of them mean "not opted in", because the alternative is measuring (and alerting on) tenants
///     that never asked for it.
/// </summary>
public class TenantObservabilityOptInTests
{
    private const string TenantId = "acme";

    [Fact]
    public async Task ReturnsTrue_WhenTheAttributeIsSet()
    {
        var sut = CreateSut(Configuration(true));

        (await sut.IsCkModelObservabilityEnabledAsync(TenantId, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task ReturnsFalse_WhenTheAttributeIsFalse()
    {
        var sut = CreateSut(Configuration(false));

        (await sut.IsCkModelObservabilityEnabledAsync(TenantId, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ReturnsFalse_WhenTheAttributeIsAbsent()
    {
        // The attribute is optional and only exists from System-2.3.0 on: every tenant on an older
        // System model lands here, and every one of them must be skipped.
        var sut = CreateSut(new RtEntity());

        (await sut.IsCkModelObservabilityEnabledAsync(TenantId, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ReturnsFalse_WhenTheTenantHasNoModeConfiguration()
    {
        var sut = CreateSut(null);

        (await sut.IsCkModelObservabilityEnabledAsync(TenantId, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ReturnsFalse_WhenTheTenantIsUnknown()
    {
        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.TryFindTenantContextAsync(TenantId)).Returns(Task.FromResult<ITenantContext?>(null));

        var sut = new TenantObservabilityOptIn(systemContext);

        (await sut.IsCkModelObservabilityEnabledAsync(TenantId, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ReturnsTrue_WhenTheAttributeArrivedAsAStringFromAnImport()
    {
        // A blueprint seed lands through ImportRt, which round-trips attribute values through JSON;
        // booleans have come back as strings before. GetAttributeValueOrStandard coerces, and this
        // pins that a seeded tenant is not silently skipped.
        var entity = new RtEntity(SystemCkIds.RtCkTenantModeConfigurationTypeId, OctoObjectId.GenerateNewId(),
            new Dictionary<string, object?>
            {
                { TenantObservabilityOptIn.PublishCkModelObservabilityAttribute, "true" }
            });

        var sut = CreateSut(entity);

        (await sut.IsCkModelObservabilityEnabledAsync(TenantId, CancellationToken.None)).Should().BeTrue();
    }

    private static RtEntity Configuration(bool publish)
    {
        var entity = new RtEntity();
        entity.SetAttributeValue(TenantObservabilityOptIn.PublishCkModelObservabilityAttribute,
            AttributeValueTypesDto.Boolean, publish);
        return entity;
    }

    private static TenantObservabilityOptIn CreateSut(RtEntity? configuration)
    {
        var repository = A.Fake<ITenantRepository>();
        var resultSet = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => resultSet.Items)
            .Returns(configuration == null ? [] : new[] { configuration });
        A.CallTo(() => repository.GetRtEntitiesByTypeAsync(
                A<IOctoSession>._, A<RtCkId<CkTypeId>>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Returns(resultSet);

        var tenantContext = A.Fake<ITenantContext>();
        A.CallTo(() => tenantContext.GetTenantRepository()).Returns(repository);

        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.TryFindTenantContextAsync(TenantId)).Returns(tenantContext);

        return new TenantObservabilityOptIn(systemContext);
    }
}
