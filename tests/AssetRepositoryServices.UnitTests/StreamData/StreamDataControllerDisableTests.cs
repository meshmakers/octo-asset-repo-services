using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AssetRepositoryServices.UnitTests.StreamData;

/// <summary>
///     HTTP mapping of <c>StreamDataController.Disable</c> (AB#4255): the engine's refusal while archives
///     are still Activated answers 409 with the reason plus remediation verbs in an
///     <see cref="OperationFailedErrorDto" />, every other stream data / configuration error stays a 400,
///     and anything else propagates.
/// </summary>
public class StreamDataControllerDisableTests
{
    private const string TenantId = "maco";

    private readonly ITenantContext _tenantContext = A.Fake<ITenantContext>();
    private readonly StreamDataController _controller;

    public StreamDataControllerDisableTests()
    {
        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.FindTenantContextAsync(TenantId)).Returns(_tenantContext);

        _controller = new StreamDataController(
            A.Fake<ILogger<StreamDataController>>(),
            systemContext,
            Options.Create(new StreamDataInstanceConfiguration { Enabled = true }),
            A.Fake<IHostApplicationLifetime>());
    }

    [Fact]
    public async Task Disable_ReturnsNoContent_WhenTheEngineDisables()
    {
        var result = await _controller.Disable(TenantId);

        result.Should().BeOfType<NoContentResult>();
        A.CallTo(() => _tenantContext.DisableStreamDataAsync()).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Disable_ReturnsConflictNamingTheArchivesAndTheRemediation_WhenTheEngineRefuses()
    {
        var archive = new ArchiveSnapshot(OctoObjectId.GenerateNewId(), new RtCkId<CkTypeId>("Test/MeteringPoint"),
            CkArchiveStatus.Activated, "temps", Array.Empty<CkArchiveColumnSpec>());
        A.CallTo(() => _tenantContext.DisableStreamDataAsync())
            .Throws(StreamDataDisableBlockedException.Create(TenantId, [archive]));

        var result = await _controller.Disable(TenantId);

        var error = result.Should().BeOfType<ConflictObjectResult>().Subject
            .Value.Should().BeOfType<OperationFailedErrorDto>().Subject;
        error.Message.Should().StartWith($"Stream data cannot be disabled for tenant '{TenantId}'")
            .And.Contain("RawArchive 'temps' (Activated)")
            .And.Contain("DisableArchive")
            .And.Contain("DeleteArchive")
            .And.Contain("Repository > Archives")
            .And.EndWith("then retry DisableStreamData.");
    }

    [Fact]
    public async Task Disable_ReturnsBadRequest_ForOtherStreamDataErrors()
    {
        A.CallTo(() => _tenantContext.DisableStreamDataAsync())
            .Throws(new StreamDataNotEnabledException("StreamData is disabled at the instance level."));

        var result = await _controller.Disable(TenantId);

        result.Should().BeOfType<BadRequestObjectResult>()
            .Which.Value.Should().Be("StreamData is disabled at the instance level.");
    }

    [Fact]
    public async Task Disable_ReturnsBadRequest_ForConfigurationErrors()
    {
        A.CallTo(() => _tenantContext.DisableStreamDataAsync())
            .Throws(ConfigurationException.TenantIsAutoEnabled(TenantId));

        var result = await _controller.Disable(TenantId);

        result.Should().BeOfType<BadRequestObjectResult>()
            .Which.Value.Should().Be($"Tenant '{TenantId}' is auto enabled.");
    }

    [Fact]
    public async Task Disable_Propagates_WhenTheArchiveStateCannotBeRead()
    {
        // An unreadable state must never be answered as a successful (or merely refused) disable.
        A.CallTo(() => _tenantContext.DisableStreamDataAsync())
            .Throws(new InvalidOperationException("archive store unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _controller.Disable(TenantId));
    }
}
