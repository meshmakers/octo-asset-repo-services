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
///     <c>Archive.RedactHiddenColumns-2</c> passes the confirmed columns to the engine's guarded redaction - never to the
///     unguarded one - and answers a changed column set with its own code and both sets.
/// </summary>
public class ArchiveRedactHiddenColumns2HandlerTests
{
    private const string TenantId = "t1";
    private static readonly OctoObjectId ArchiveId = OctoObjectId.GenerateNewId();

    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly ITenantContext _tenant = A.Fake<ITenantContext>();
    private readonly IArchiveLifecycleService _lifecycle = A.Fake<IArchiveLifecycleService>();

    public ArchiveRedactHiddenColumns2HandlerTests()
    {
        A.CallTo(() => _systemContext.TryFindTenantContextAsync(TenantId)).Returns(_tenant);
        A.CallTo(() => _tenant.GetArchiveLifecycleService()).Returns(_lifecycle);
    }

    private ArchiveRedactHiddenColumns2Handler Sut() =>
        new(_systemContext, NullLogger<ArchiveRedactHiddenColumns2Handler>.Instance);

    private static CkMethodContext<ArchiveRedactHiddenColumns2Parameters> Call(bool confirm,
        IReadOnlyList<string>? expected, string? target = null) =>
        new(new CkMethodRequest(TenantId)
        {
            InvocationId = Guid.NewGuid(), MethodId = SystemStreamDataCkIds.ArchiveRedactHiddenColumns2MethodId,
            CkTypeId = "System.StreamData/RawArchive", TargetRtId = target ?? ArchiveId.ToString(),
            DeadlineUtc = DateTime.UtcNow.AddSeconds(30)
        }, new ArchiveRedactHiddenColumns2Parameters { ConfirmDataLoss = confirm, ExpectedColumns = expected! });

    [Fact]
    public async Task Confirmed_PassesTheConfirmedColumns_AndAnswersTheDroppedColumns()
    {
        A.CallTo(() => _lifecycle.RedactConfirmedHiddenColumnsAsync(ArchiveId,
                A<IReadOnlyCollection<string>>.That.IsSameSequenceAs(new[] { "Pin", "Reading" })))
            .Returns(new ArchiveRedactionResult(ArchiveId, CkArchiveStatus.Activated, ["Pin", "Reading"]));

        var outcome = await Sut().HandleAsync(Call(confirm: true, ["Pin", "Reading"]), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        var value = outcome.Value!.Value!;
        value.ArchiveRtId.Should().Be(ArchiveId.ToString());
        value.Status.Should().Be(RtCkArchiveStatusEnum.Activated);
        value.RemovedColumns!.Should().Equal("Pin", "Reading");
        A.CallTo(() => _lifecycle.RedactHiddenColumnsAsync(A<OctoObjectId>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task AChangedColumnSet_AnswersItsCode_WithBothSets()
    {
        A.CallTo(() => _lifecycle.RedactConfirmedHiddenColumnsAsync(ArchiveId, A<IReadOnlyCollection<string>>._))
            .Throws(new HiddenColumnRedactionColumnsChangedException(ArchiveId, ["Pin"], ["Pin", "Reading"]));

        var outcome = await Sut().HandleAsync(Call(confirm: true, ["Pin"]), CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.Failure!.Code.Should().Be(ArchiveMethodErrors.RedactionColumnsChanged);
        outcome.Failure.Parameters["expectedColumns"].Should().Be("Pin");
        outcome.Failure.Parameters["currentColumns"].Should().Be("Pin,Reading");
        outcome.Failure.Parameters["archiveRtId"].Should().Be(ArchiveId.ToString());
        ArchiveRedactHiddenColumns2Handler.Redact2DeclaredCodes.Should().Contain(ArchiveMethodErrors.RedactionColumnsChanged);
    }

    [Fact]
    public async Task WithoutTheConfirmation_NothingIsCalled()
    {
        var outcome = await Sut().HandleAsync(Call(confirm: false, ["Pin"]), CancellationToken.None);

        outcome.Failure!.Code.Should().Be(ArchiveMethodErrors.ConfirmationRequired);
        A.CallTo(_lifecycle).MustNotHaveHappened();
    }

    [Fact]
    public async Task AMissingList_ConfirmsNoColumn_AndNeverFallsBackToTheUnguardedRedaction()
    {
        A.CallTo(() => _lifecycle.RedactConfirmedHiddenColumnsAsync(ArchiveId, A<IReadOnlyCollection<string>>._))
            .Throws(new HiddenColumnRedactionColumnsChangedException(ArchiveId, [], ["Pin"]));

        var outcome = await Sut().HandleAsync(Call(confirm: true, expected: null), CancellationToken.None);

        outcome.Failure!.Code.Should().Be(ArchiveMethodErrors.RedactionColumnsChanged);
        A.CallTo(() => _lifecycle.RedactConfirmedHiddenColumnsAsync(ArchiveId,
            A<IReadOnlyCollection<string>>.That.IsEmpty())).MustHaveHappenedOnceExactly();
        A.CallTo(() => _lifecycle.RedactHiddenColumnsAsync(A<OctoObjectId>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ABlockedRedaction_AnswersItsCode()
    {
        A.CallTo(() => _lifecycle.RedactConfirmedHiddenColumnsAsync(ArchiveId, A<IReadOnlyCollection<string>>._))
            .Throws(new HiddenColumnRedactionBlockedException(ArchiveId, ["Pin"], [OctoObjectId.GenerateNewId()]));

        var outcome = await Sut().HandleAsync(Call(confirm: true, ["Pin"]), CancellationToken.None);

        outcome.Failure!.Code.Should().Be(ArchiveMethodErrors.RedactionBlocked);
    }

    [Fact]
    public async Task AMalformedTarget_IsNotFound()
    {
        var outcome = await Sut().HandleAsync(Call(confirm: true, ["Pin"], target: "nope"), CancellationToken.None);

        outcome.Failure!.Code.Should().Be(CkMethodErrorCodes.MethodTargetNotFound);
        A.CallTo(_lifecycle).MustNotHaveHappened();
    }
}
