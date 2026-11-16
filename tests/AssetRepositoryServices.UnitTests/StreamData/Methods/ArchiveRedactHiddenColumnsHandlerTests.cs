using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AssetRepositoryServices.UnitTests.StreamData.Methods;

/// <summary>
///     <c>Archive.RedactHiddenColumns-1</c> drops columns and their values, so it runs only with an explicit
///     confirmation, answers the dropped columns and the status, and maps a blocked redaction to its own code.
/// </summary>
public class ArchiveRedactHiddenColumnsHandlerTests
{
    private const string TenantId = "t1";
    private static readonly OctoObjectId ArchiveId = OctoObjectId.GenerateNewId();

    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly ITenantContext _tenant = A.Fake<ITenantContext>();
    private readonly IArchiveLifecycleService _lifecycle = A.Fake<IArchiveLifecycleService>();

    public ArchiveRedactHiddenColumnsHandlerTests()
    {
        A.CallTo(() => _systemContext.TryFindTenantContextAsync(TenantId)).Returns(_tenant);
        A.CallTo(() => _tenant.GetArchiveLifecycleService()).Returns(_lifecycle);
    }

    private ArchiveRedactHiddenColumnsHandler Sut() =>
        new(_systemContext, NullLogger<ArchiveRedactHiddenColumnsHandler>.Instance);

    private static CkMethodContext<ArchiveRedactHiddenColumnsParameters> Call(bool confirm, string? target = null) =>
        new(new CkMethodRequest(TenantId)
        {
            InvocationId = Guid.NewGuid(), MethodId = SystemStreamDataCkIds.ArchiveRedactHiddenColumnsMethodId,
            CkTypeId = "System.StreamData/RawArchive", TargetRtId = target ?? ArchiveId.ToString(),
            DeadlineUtc = DateTime.UtcNow.AddSeconds(30)
        }, new ArchiveRedactHiddenColumnsParameters { ConfirmDataLoss = confirm });

    [Fact]
    public async Task WithoutTheConfirmation_NothingIsDropped()
    {
        var outcome = await Sut().HandleAsync(Call(confirm: false), CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.Failure!.Code.Should().Be(ArchiveMethodErrors.ConfirmationRequired);
        outcome.Failure.Parameters["archiveRtId"].Should().Be(ArchiveId.ToString());
        A.CallTo(() => _lifecycle.RedactHiddenColumnsAsync(A<OctoObjectId>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ACodeTheMethodDoesNotDeclare_BecomesStreamDataError_WithTheMessage()
    {
        A.CallTo(() => _lifecycle.RedactHiddenColumnsAsync(ArchiveId))
            .Throws(new InvalidArchiveStateTransitionException(ArchiveId, CkArchiveStatus.Activated, "redact"));

        var outcome = await Sut().HandleAsync(Call(confirm: true), CancellationToken.None);

        outcome.Failure!.Code.Should().Be(Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Statics.GraphQlErrorStreamData);
        outcome.Failure.Parameters[ArchiveMethodErrors.MessageParameter].Should().Contain("redact");
    }

    [Fact]
    public async Task Confirmed_AnswersTheDroppedColumnsAndTheStatus()
    {
        A.CallTo(() => _lifecycle.RedactHiddenColumnsAsync(ArchiveId))
            .Returns(new ArchiveRedactionResult(ArchiveId, CkArchiveStatus.Activated, ["Pin", "Reading"]));

        var outcome = await Sut().HandleAsync(Call(confirm: true), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        var value = outcome.Value!.Value!;
        value.ArchiveRtId.Should().Be(ArchiveId.ToString());
        value.Status.Should().Be(RtCkArchiveStatusEnum.Activated);
        value.RemovedColumns!.Should().Equal("Pin", "Reading");
    }

    [Fact]
    public async Task ABlockedRedaction_AnswersItsCode_WithTheColumnsAndTheRollups()
    {
        var rollup = OctoObjectId.GenerateNewId();
        A.CallTo(() => _lifecycle.RedactHiddenColumnsAsync(ArchiveId))
            .Throws(new HiddenColumnRedactionBlockedException(ArchiveId, ["Pin"], [rollup]));

        var outcome = await Sut().HandleAsync(Call(confirm: true), CancellationToken.None);

        outcome.Failure!.Code.Should().Be(ArchiveMethodErrors.RedactionBlocked);
        outcome.Failure.Parameters["columns"].Should().Be("Pin");
        outcome.Failure.Parameters["rollupRtIds"].Should().Be(rollup.ToString());
    }

    [Fact]
    public async Task AMalformedTarget_IsNotFound()
    {
        var outcome = await Sut().HandleAsync(Call(confirm: true, target: "nope"), CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.Failure!.Code.Should().Be(CkMethodErrorCodes.MethodTargetNotFound);
        A.CallTo(() => _lifecycle.RedactHiddenColumnsAsync(A<OctoObjectId>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task WithoutStreamData_TheCallIsRefused()
    {
        A.CallTo(() => _tenant.GetArchiveLifecycleService()).Returns(null);

        var outcome = await Sut().HandleAsync(Call(confirm: true), CancellationToken.None);

        outcome.Failure!.Code.Should().Be(ArchiveMethodErrors.StreamDataNotEnabled);
    }
}
