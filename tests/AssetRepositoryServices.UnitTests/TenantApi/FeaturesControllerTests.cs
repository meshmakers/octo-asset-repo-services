using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Xunit;

namespace AssetRepositoryServices.UnitTests.TenantApi;

/// <summary>
///     AB#4884 — the Tenant Features panel reads one aggregate status whose per-capability flags come
///     from the same reader the delete/detach guard evaluates (AB#4255), so panel and guard never
///     disagree. Availability ("installed at all") is deliberately not part of this endpoint — it
///     comes from the <c>_configuration</c> document — except for Stream Data's instance kill switch,
///     which lives in this service.
/// </summary>
public class FeaturesControllerTests
{
    private const string TenantId = "maco";

    private readonly ITenantContext _tenantContext = A.Fake<ITenantContext>();
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly ITenantCapabilityStateReader _capabilityStateReader = A.Fake<ITenantCapabilityStateReader>();

    public FeaturesControllerTests()
    {
        A.CallTo(() => _systemContext.FindTenantContextAsync(TenantId)).Returns(_tenantContext);
    }

    private FeaturesController CreateController(bool streamDataInstanceEnabled = true) => new(
        _systemContext,
        _capabilityStateReader,
        Options.Create(new StreamDataInstanceConfiguration { Enabled = streamDataInstanceEnabled }));

    private void SetupEnabledCapabilities(params TenantCapability[] enabled) =>
        A.CallTo(() => _capabilityStateReader.GetEnabledCapabilitiesAsync(_tenantContext))
            .Returns(enabled);

    private static TenantFeaturesStatusDto Unwrap(ActionResult<TenantFeaturesStatusDto> result) =>
        result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<TenantFeaturesStatusDto>().Subject;

    [Fact]
    public async Task Status_ReportsEveryCapabilityFlag_FromTheGuardsReader()
    {
        SetupEnabledCapabilities(TenantCapability.StreamData, TenantCapability.Communication);

        var status = Unwrap(await CreateController().Status(TenantId));

        status.StreamData.TenantEnabled.Should().BeTrue();
        status.Communication.TenantEnabled.Should().BeTrue();
        status.Reporting.TenantEnabled.Should().BeFalse();
        status.AiServices.TenantEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Status_ReportsAllDisabled_WhenNoFlagIsSet()
    {
        SetupEnabledCapabilities();

        var status = Unwrap(await CreateController().Status(TenantId));

        status.StreamData.TenantEnabled.Should().BeFalse();
        status.Communication.TenantEnabled.Should().BeFalse();
        status.Reporting.TenantEnabled.Should().BeFalse();
        status.AiServices.TenantEnabled.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Status_ReportsTheStreamDataInstanceFlag(bool instanceEnabled)
    {
        SetupEnabledCapabilities();

        var status = Unwrap(await CreateController(instanceEnabled).Status(TenantId));

        status.StreamData.InstanceEnabled.Should().Be(instanceEnabled);
    }

    [Fact]
    public async Task Status_ReportsTheTenantFlag_EvenWhenTheInstanceSwitchIsOff()
    {
        // A tenant left enabled on an installation without stream data still blocks delete/detach —
        // the panel must be able to show exactly that instead of a false "disabled".
        SetupEnabledCapabilities(TenantCapability.StreamData);

        var status = Unwrap(await CreateController(streamDataInstanceEnabled: false).Status(TenantId));

        status.StreamData.InstanceEnabled.Should().BeFalse();
        status.StreamData.TenantEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Status_PropagatesReadFailures()
    {
        // An unreadable state must never be shown as "disabled" while the guard would still refuse.
        A.CallTo(() => _capabilityStateReader.GetEnabledCapabilitiesAsync(_tenantContext))
            .Throws(new InvalidOperationException("configuration store unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateController().Status(TenantId));
    }
}
