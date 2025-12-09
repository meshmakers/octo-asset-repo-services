using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.System;

[Collection("Sequential")]
public class SampleDataTests(SampleDataFixture fixture)
    : IClassFixture<SampleDataFixture>
{
    [Fact]
    public async Task ConstructionKit_ShouldBeImported()
    {
        // Arrange
        var systemContext = fixture.GetSystemContext();

        // Act
        var modelExists = await systemContext.IsCkModelExistingAsync(new CkModelId("AssetRepositoryIntegrationTest"));

        // Assert
        Assert.True(modelExists);
    }

    [Fact]
    public async Task Customers_ShouldBeImported()
    {
        // Arrange
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        // Act
        var queryOptions = RtEntityQueryOptions.Create();
        var result = await tenantRepository.GetRtEntitiesByTypeAsync(session, "AssetRepositoryIntegrationTest/Customer", queryOptions);

        await session.CommitTransactionAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(4, result.TotalCount); // 4 customers imported

        // Verify specific customers by well-known name
        var wellKnownNames = result.Items.Select(c => c.RtWellKnownName).ToList();
        Assert.Contains("CustomerMaxMustermann", wellKnownNames);
        Assert.Contains("CustomerTechGmbH", wellKnownNames);
    }

    [Fact]
    public async Task OperatingFacilities_ShouldBeImported()
    {
        // Arrange
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        // Act
        var queryOptions = RtEntityQueryOptions.Create();
        var result = await tenantRepository.GetRtEntitiesByTypeAsync(session, "AssetRepositoryIntegrationTest/OperatingFacility", queryOptions);

        await session.CommitTransactionAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(4, result.TotalCount); // 4 operating facilities imported

        // Verify specific facility exists
        var wellKnownNames = result.Items.Select(f => f.RtWellKnownName).ToList();
        Assert.Contains("FacilityHauptstrasse42", wellKnownNames);
    }

    [Fact]
    public async Task MeteringPoints_ShouldBeImported()
    {
        // Arrange
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        // Act
        var queryOptions = RtEntityQueryOptions.Create();
        var result = await tenantRepository.GetRtEntitiesByTypeAsync(session, "AssetRepositoryIntegrationTest/MeteringPoint", queryOptions);

        await session.CommitTransactionAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(8, result.TotalCount); // 8 metering points imported

        // Verify specific metering points exist
        var wellKnownNames = result.Items.Select(mp => mp.RtWellKnownName).ToList();
        Assert.Contains("MeteringPointAT0010001234567890", wellKnownNames);
    }

    [Fact]
    public async Task Ownership_Associations_ShouldExist()
    {
        // Arrange
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        // Act - Get facilities
        var queryOptions = RtEntityQueryOptions.Create();
        var facilityResult = await tenantRepository.GetRtEntitiesByTypeAsync(session, "AssetRepositoryIntegrationTest/OperatingFacility", queryOptions);

        var hauptstrasseFacility = facilityResult.Items.First(f =>
            f.RtWellKnownName == "FacilityHauptstrasse42");

        // Get associations using RtAssociationQueryOptions
        var associationOptions = RtAssociationExtendedQueryOptions.Create(GraphDirections.Outbound, 0, 10);
        var associations = await tenantRepository.GetRtAssociationsAsync(
            session,
            [hauptstrasseFacility.ToRtEntityId()],
            associationOptions);

        await session.CommitTransactionAsync();

        // Assert
        Assert.NotNull(associations);
        Assert.NotEmpty(associations);

        // Verify associations exist for this facility
        var facilityAssocs = associations[hauptstrasseFacility.ToRtEntityId()];
        Assert.NotNull(facilityAssocs);
        Assert.NotEmpty(facilityAssocs.Items);
    }

    [Fact]
    public async Task ParentChild_Associations_ShouldExist()
    {
        // Arrange
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        // Act - Get metering points
        var queryOptions = RtEntityQueryOptions.Create();
        var meteringResult = await tenantRepository.GetRtEntitiesByTypeAsync(session, "AssetRepositoryIntegrationTest/MeteringPoint", queryOptions);

        // Get associations to verify parent-child relationships
        var meteringPointIds = meteringResult.Items.Take(2).Select(mp => mp.ToRtEntityId()).ToList();
        var associationOptions = RtAssociationExtendedQueryOptions.Create(GraphDirections.Outbound, 0, 10);
        var associations = await tenantRepository.GetRtAssociationsAsync(
            session,
            meteringPointIds,
            associationOptions);

        await session.CommitTransactionAsync();

        // Assert - Verify associations exist
        Assert.NotNull(associations);
        Assert.NotEmpty(associations);
    }
}
