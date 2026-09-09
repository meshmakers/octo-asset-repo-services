using System.Text.Json;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
/// AB#5157 — the <c>createRollupArchive</c> input contract end to end through the real GraphQL
/// schema and the real engine (TC-API-01 … TC-API-11).
/// <para>
/// Two accepted source forms, exactly one at a time: the new <c>sources</c> list with half-open
/// validity spans, and the deprecated <c>sourceArchiveRtId</c> scalar folded into one unbounded
/// entry. Supplying both or neither is a CLIENT input error the resolver raises itself
/// (<c>ASSET1004</c> / MODEL_VALIDATION_ERRORS) before the engine is called; every rule the engine
/// owns — duplicate source, inverted / overlapping spans, a second open start or open end, a
/// boundary off the bucket grid, a source targeting another CK type — surfaces as
/// <c>STREAMDATA_ERROR</c> with the engine message, which always names the offending source archive.
/// </para>
/// <para>
/// The two activation-time rules (strict aggregation path, transitive cycle) cannot be reached
/// through create — the create-time validator does not load the sources' column lists, and a cycle
/// cannot be built with a rollup that does not exist yet. They are proven here through
/// <c>activateArchive</c>, the cycle on a source list rewritten the way an <c>ImportRt</c> seed
/// writes it (straight onto the entity, bypassing the lifecycle service).
/// </para>
/// </summary>
[Collection(StreamDataMutatingCollection.Name)]
public class CreateRollupArchiveInputTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    private const string CreateMutation = @"
        mutation ($input: CreateRollupArchiveInput!) {
          streamData { createRollupArchive(input: $input) }
        }";

    private const string ActivateMutation = @"
        mutation ($rtId: OctoObjectId!) {
          streamData { activateArchive(rtId: $rtId) { archiveRtId status } }
        }";

    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);
    private static readonly DateTime H0 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── accepted forms ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SourcesOnly_Succeeds_AndStoresEverySpanVerbatim()
    {
        fixture.OutputHelper = output;
        var legacy = await CreateRawArchiveAsync("InputLegacy", activate: false);

        var rtId = await CreateSucceedsAsync(Input(
            name: "SourcesOnly",
            sources:
            [
                Source(legacy, validTo: H0.AddHours(2)),
                Source(fixture.ArchiveRtId, validFrom: H0.AddHours(2)),
            ]));

        var snapshot = await LoadRollupAsync(rtId);
        snapshot.Sources.Should().HaveCount(2);
        snapshot.Sources[0].SourceArchiveRtId.Should().Be(legacy);
        snapshot.Sources[0].ValidFrom.Should().BeNull("an omitted validFrom stays an open start");
        snapshot.Sources[0].ValidTo.Should().Be(H0.AddHours(2));
        snapshot.Sources[1].SourceArchiveRtId.Should().Be(fixture.ArchiveRtId);
        snapshot.Sources[1].ValidFrom.Should().Be(H0.AddHours(2));
        snapshot.Sources[1].ValidTo.Should().BeNull();

        snapshot.SingleUnboundedSourceRtId.Should().BeNull(
            "the derived single-source scalar is only defined for exactly one unbounded source");
        snapshot.HasSource(legacy).Should().BeTrue();
        snapshot.HasSource(fixture.ArchiveRtId).Should().BeTrue();
    }

    [Fact]
    public async Task DeprecatedScalarOnly_Succeeds_AndBecomesOneUnboundedSource()
    {
        fixture.OutputHelper = output;

        var rtId = await CreateSucceedsAsync(Input(
            name: "ScalarOnly",
            sourceArchiveRtId: fixture.ArchiveRtId));

        var snapshot = await LoadRollupAsync(rtId);
        snapshot.Sources.Should().ContainSingle();
        snapshot.Sources[0].SourceArchiveRtId.Should().Be(fixture.ArchiveRtId);
        snapshot.Sources[0].IsUnbounded.Should().BeTrue();
        snapshot.SingleUnboundedSourceRtId.Should().Be(fixture.ArchiveRtId,
            "one unbounded source keeps the deprecated projection populated for legacy clients");
    }

    // ── resolver-owned input errors ───────────────────────────────────────────────────────────

    [Fact]
    public async Task BothForms_AreRejected_WithTheResolversOwnMessage()
    {
        fixture.OutputHelper = output;

        var error = await CreateFailsAsync(Input(
            name: "BothForms",
            sourceArchiveRtId: fixture.ArchiveRtId,
            sources: [Source(fixture.ArchiveRtId)]));

        error.Code.Should().Be(Statics.GraphQlModelValidationErrors);
        error.Message.Should().Be(
            "createRollupArchive: supply either 'sources' or the deprecated 'sourceArchiveRtId', not both.");
    }

    [Fact]
    public async Task NeitherForm_IsRejected_WithTheResolversOwnMessage()
    {
        fixture.OutputHelper = output;

        var error = await CreateFailsAsync(Input(name: "NoSource"));

        error.Code.Should().Be(Statics.GraphQlModelValidationErrors);
        error.Message.Should().Be(
            "createRollupArchive: at least one source archive is required — supply 'sources' (or the deprecated 'sourceArchiveRtId').");
    }

    [Fact]
    public async Task AnExplicitlyEmptySourcesList_IsRejected_AsAnEmptyListNotAnAbsentOne()
    {
        fixture.OutputHelper = output;

        var error = await CreateFailsAsync(Input(name: "EmptySources", sources: []));

        error.Code.Should().Be(Statics.GraphQlModelValidationErrors);
        error.Message.Should().Be(
            "createRollupArchive: sources must contain at least one entry; omit it to use the deprecated sourceArchiveRtId.");
    }

    [Fact]
    public async Task AnExplicitlyEmptySourcesList_WithTheDeprecatedScalar_IsRejected_NotSilentlyAcceptedAsScalarOnly()
    {
        // An empty list is a declared intent to use 'sources'. Falling back to the deprecated scalar
        // here would create a rollup the client never asked for.
        fixture.OutputHelper = output;

        var error = await CreateFailsAsync(Input(
            name: "EmptySourcesPlusScalar",
            sourceArchiveRtId: fixture.ArchiveRtId,
            sources: []));

        error.Code.Should().Be(Statics.GraphQlModelValidationErrors);
        error.Message.Should().Be(
            "createRollupArchive: sources must contain at least one entry; omit it to use the deprecated sourceArchiveRtId.");
    }

    // ── engine-owned source rules ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DuplicateSource_IsRejected_NamingTheRepeatedArchive()
    {
        fixture.OutputHelper = output;

        var error = await CreateFailsAsync(Input(
            name: "DuplicateSource",
            sources:
            [
                Source(fixture.ArchiveRtId, validTo: H0.AddHours(2)),
                Source(fixture.ArchiveRtId, validFrom: H0.AddHours(2)),
            ]));

        error.Code.Should().Be(Statics.GraphQlErrorStreamData);
        error.Message.Should().Contain($"references source archive '{fixture.ArchiveRtId}' more than once");
    }

    [Fact]
    public async Task InvertedSpan_IsRejected_NamingTheSourceAndBothBounds()
    {
        fixture.OutputHelper = output;

        var error = await CreateFailsAsync(Input(
            name: "InvertedSpan",
            sources: [Source(fixture.ArchiveRtId, validFrom: H0.AddHours(3), validTo: H0.AddHours(1))]));

        error.Code.Should().Be(Statics.GraphQlErrorStreamData);
        error.Message.Should().Contain($"the validity span of source archive '{fixture.ArchiveRtId}' is empty")
            .And.Contain("must be earlier than ValidTo");
    }

    [Fact]
    public async Task OverlappingSpans_AreRejected_NamingBothSources()
    {
        fixture.OutputHelper = output;
        var other = await CreateRawArchiveAsync("InputOverlap", activate: false);

        var error = await CreateFailsAsync(Input(
            name: "OverlappingSpans",
            sources:
            [
                Source(fixture.ArchiveRtId, validFrom: H0, validTo: H0.AddHours(4)),
                Source(other, validFrom: H0.AddHours(2), validTo: H0.AddHours(6)),
            ]));

        error.Code.Should().Be(Statics.GraphQlErrorStreamData);
        error.Message.Should().Contain($"the validity span of source archive '{other}' overlaps")
            .And.Contain($"the span of source archive '{fixture.ArchiveRtId}'")
            .And.Contain("Source spans must be pairwise disjoint.");
    }

    [Fact]
    public async Task TwoOpenStarts_AreRejected_NamingTheStartSide()
    {
        fixture.OutputHelper = output;
        var other = await CreateRawArchiveAsync("InputOpenStart", activate: false);

        var error = await CreateFailsAsync(Input(
            name: "TwoOpenStarts",
            sources:
            [
                Source(fixture.ArchiveRtId, validTo: H0.AddHours(2)),
                Source(other, validTo: H0.AddHours(4)),
            ]));

        error.Code.Should().Be(Statics.GraphQlErrorStreamData);
        error.Message.Should().Contain($"source archive '{other}'")
            .And.Contain($"source archive '{fixture.ArchiveRtId}'")
            .And.Contain("both leave the start of their validity span open")
            .And.Contain("At most one source may have an open start (no ValidFrom).");
    }

    [Fact]
    public async Task TwoOpenEnds_AreRejected_NamingTheEndSide()
    {
        fixture.OutputHelper = output;
        var other = await CreateRawArchiveAsync("InputOpenEnd", activate: false);

        var error = await CreateFailsAsync(Input(
            name: "TwoOpenEnds",
            sources:
            [
                Source(fixture.ArchiveRtId, validFrom: H0),
                Source(other, validFrom: H0.AddHours(4)),
            ]));

        error.Code.Should().Be(Statics.GraphQlErrorStreamData);
        error.Message.Should().Contain("both leave the end of their validity span open")
            .And.Contain("At most one source may have an open end (no ValidTo).");
    }

    [Fact]
    public async Task ASpanBoundaryOffTheBucketGrid_IsRejected_NamingTheOffendingBound()
    {
        // 1 h buckets on the tick-zero grid: 00:30 splits a bucket between two sources.
        fixture.OutputHelper = output;
        var other = await CreateRawArchiveAsync("InputOffGrid", activate: false);
        var offGrid = H0.AddMinutes(30);

        var error = await CreateFailsAsync(Input(
            name: "OffGridBoundary",
            sources:
            [
                Source(fixture.ArchiveRtId, validTo: offGrid),
                Source(other, validFrom: offGrid),
            ]));

        error.Code.Should().Be(Statics.GraphQlErrorStreamData);
        error.Message.Should().Contain("does not lie on a bucket boundary of the rollup")
            .And.Contain("ValidTo")
            .And.Contain($"of source archive '{fixture.ArchiveRtId}'",
                "the first source's ValidTo is the first bound the validator walks");
    }

    [Fact]
    public async Task ASourceTargetingAnotherCkType_IsRejected_NamingBothTypes()
    {
        fixture.OutputHelper = output;
        var foreignType = await CreateForeignTypeArchiveAsync("InputForeignType");

        var error = await CreateFailsAsync(Input(
            name: "TargetTypeMismatch",
            sources:
            [
                Source(fixture.ArchiveRtId, validTo: H0.AddHours(2)),
                Source(foreignType, validFrom: H0.AddHours(2)),
            ]));

        error.Code.Should().Be(Statics.GraphQlErrorStreamData);
        error.Message.Should().Contain($"source archive '{foreignType}' targets CK type")
            .And.Contain("AssetRepositoryIntegrationTest/Product")
            .And.Contain(fixture.TestCkTypeId)
            .And.Contain("All sources must target the same CK type.");
    }

    // ── activation-time rules ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnAggregationPathMissingOnOneSource_IsRejectedAtActivation_NamingThatSource()
    {
        // Strict rule (decision 2): the path must resolve on EVERY source, otherwise the buckets
        // that source serves would silently stay empty. Create still succeeds — it never loads the
        // sources' column lists.
        fixture.OutputHelper = output;
        var full = await CreateRawArchiveAsync("InputPathFull", activate: true, "Voltage", "Current");
        var partial = await CreateRawArchiveAsync("InputPathPartial", activate: true, "Voltage");

        var rtId = await CreateSucceedsAsync(Input(
            name: "PathMissing",
            sources:
            [
                Source(full, validTo: H0.AddHours(2)),
                Source(partial, validFrom: H0.AddHours(2)),
            ],
            aggregations: [Aggregation("Current", "SUM")]));

        var error = await ActivateFailsAsync(rtId);

        error.Code.Should().Be(Statics.GraphQlErrorStreamData);
        error.Message.Should().Contain("aggregation source path 'Current' does not resolve on")
            .And.Contain($"source archive '{partial}'")
            .And.Contain("Every aggregation path must resolve on every source.");
    }

    [Fact]
    public async Task ADisabledSource_IsRejectedAtActivation_NamingItsStatus()
    {
        fixture.OutputHelper = output;
        var source = await CreateRawArchiveAsync("InputDisabledSource", activate: true);
        var rtId = await CreateSucceedsAsync(Input(name: "DisabledSource", sources: [Source(source)]));

        var lifecycle = (await TenantAsync()).GetArchiveLifecycleService()!;
        await lifecycle.DisableAsync(source);

        var error = await ActivateFailsAsync(rtId);

        error.Code.Should().Be(Statics.GraphQlErrorStreamData);
        error.Message.Should().Contain($"source archive '{source}' is in status Disabled; required: Activated.");
    }

    [Fact]
    public async Task ACycleClosedByASeededSourceList_IsRejectedAtActivation()
    {
        // createRollupArchive can never build a cycle (the new rollup has no rtId yet), so the
        // realistic shape is an ImportRt seed: the source list of an existing rollup is rewritten
        // straight onto the entity. Activation re-runs the transitive check and refuses.
        //
        //   raw ─▶ downstream ─▶ (seeded edge back to) upstream ─▶ downstream
        //
        // Both rollups share bucket size and column naming so the per-source rules (activated,
        // same target type, granularity, aggregation path) all pass and the cycle rule is what
        // actually fires.
        fixture.OutputHelper = output;
        var raw = await CreateRawArchiveAsync("InputCycleRaw", activate: true);

        var downstream = await CreateSucceedsAsync(Input(
            name: "CycleDownstream",
            sources: [Source(raw)],
            aggregations: [Aggregation("Voltage", "SUM", targetColumnName: "voltage")]));
        await ActivateSucceedsAsync(downstream);

        var upstream = await CreateSucceedsAsync(Input(
            name: "CycleUpstream",
            sources: [Source(downstream)],
            aggregations: [Aggregation("voltage", "SUM", targetColumnName: "voltage")]));

        // The seeded edge: downstream now claims the not-yet-activated upstream as its source.
        await SeedSourcesAsync(downstream, [new RollupSourceReference(upstream)]);

        var error = await ActivateFailsAsync(upstream);

        error.Code.Should().Be(Statics.GraphQlErrorStreamData);
        error.Message.Should().Contain("would form a cycle in the source graph through source archive")
            .And.Contain($"'{downstream}'");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> Input(
        string name,
        OctoObjectId? sourceArchiveRtId = null,
        IReadOnlyList<Dictionary<string, object?>>? sources = null,
        IReadOnlyList<Dictionary<string, object?>>? aggregations = null)
    {
        var input = new Dictionary<string, object?>
        {
            ["rtWellKnownName"] = $"{name}{Guid.NewGuid():N}",
            ["bucketSizeMs"] = (long)OneHour.TotalMilliseconds,
            ["watermarkLagMs"] = 0L,
            ["aggregations"] = aggregations ?? [Aggregation("Voltage", "SUM")],
        };

        if (sourceArchiveRtId is { } scalar)
        {
            input["sourceArchiveRtId"] = scalar.ToString();
        }

        if (sources is not null)
        {
            input["sources"] = sources;
        }

        return input;
    }

    private static Dictionary<string, object?> Source(
        OctoObjectId sourceArchiveRtId, DateTime? validFrom = null, DateTime? validTo = null)
    {
        var source = new Dictionary<string, object?>
        {
            ["sourceArchiveRtId"] = sourceArchiveRtId.ToString(),
        };
        if (validFrom is { } from) source["validFrom"] = from.ToString("o");
        if (validTo is { } to) source["validTo"] = to.ToString("o");
        return source;
    }

    private static Dictionary<string, object?> Aggregation(
        string sourcePath, string function, string? targetColumnName = null)
    {
        var aggregation = new Dictionary<string, object?>
        {
            ["sourcePath"] = sourcePath,
            ["function"] = function,
        };
        if (targetColumnName is not null) aggregation["targetColumnName"] = targetColumnName;
        return aggregation;
    }

    private async Task<OctoObjectId> CreateSucceedsAsync(Dictionary<string, object?> input)
    {
        var result = await ExecuteCreateAsync(input);
        result.Errors.Should().BeNullOrEmpty();

        var raw = JObject.Parse(fixture.SerializeGraphQl(result))
            .SelectToken("data.streamData.createRollupArchive")?.Value<string>();
        raw.Should().NotBeNullOrEmpty();
        return new OctoObjectId(raw!);
    }

    private async Task<ExecutionError> CreateFailsAsync(Dictionary<string, object?> input)
    {
        var result = await ExecuteCreateAsync(input);
        result.Errors.Should().NotBeNullOrEmpty("the payload violates a source rule");
        var error = SignificantError(result);
        fixture.OutputHelper?.WriteLine($"{error.Code}: {error.Message}");
        return error;
    }

    /// <summary>
    /// The error the resolver raised. GraphQL appends a "cannot return null for a non-null field"
    /// error because the failing resolver returns null; only the resolver's own error carries the
    /// originating exception, so that is the one to assert on.
    /// </summary>
    private static ExecutionError SignificantError(ExecutionResult result) =>
        result.Errors!.FirstOrDefault(e => e.InnerException is not null) ?? result.Errors!.First();

    private Task<ExecutionResult> ExecuteCreateAsync(Dictionary<string, object?> input) =>
        fixture.ExecuteGraphQlAsync(
            CreateMutation,
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["input"] = input }),
            StreamDataFixture.StreamDataAdminPrincipal);

    private async Task ActivateSucceedsAsync(OctoObjectId rtId)
    {
        var result = await ExecuteActivateAsync(rtId);
        result.Errors.Should().BeNullOrEmpty();
    }

    private async Task<ExecutionError> ActivateFailsAsync(OctoObjectId rtId)
    {
        var result = await ExecuteActivateAsync(rtId);
        result.Errors.Should().NotBeNullOrEmpty("activation re-runs every source rule");
        var error = SignificantError(result);
        fixture.OutputHelper?.WriteLine($"{error.Code}: {error.Message}");
        return error;
    }

    private Task<ExecutionResult> ExecuteActivateAsync(OctoObjectId rtId) =>
        fixture.ExecuteGraphQlAsync(
            ActivateMutation,
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["rtId"] = rtId.ToString() }),
            StreamDataFixture.StreamDataAdminPrincipal);

    private async Task<ITenantContext> TenantAsync()
    {
        var systemContext = fixture.GetSystemContext();
        return await systemContext.FindTenantContextAsync(systemContext.TenantId);
    }

    private async Task<RollupArchiveSnapshot> LoadRollupAsync(OctoObjectId rtId)
    {
        var snapshot = await (await TenantAsync()).GetRollupArchiveRuntimeStore()!.GetAsync(rtId);
        snapshot.Should().NotBeNull();
        return snapshot!;
    }

    /// <summary>Creates (and optionally activates) an isolated raw archive on the system tenant.</summary>
    private async Task<OctoObjectId> CreateRawArchiveAsync(
        string namePrefix, bool activate, params string[] columnPaths)
    {
        var columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>();
        foreach (var path in columnPaths.Length == 0 ? ["Voltage"] : columnPaths)
        {
            columns.Add(new RtCkArchiveColumnRecord { Path = path, Indexed = true, Required = false });
        }

        var archive = new RtRawArchive
        {
            RtWellKnownName = $"{namePrefix}{Guid.NewGuid():N}",
            TargetCkTypeId = fixture.TestCkTypeId,
            Status = RtCkArchiveStatusEnum.Created,
            Columns = columns,
        };
        await InsertAsync(archive);

        if (activate)
        {
            await (await TenantAsync()).GetArchiveLifecycleService()!.ActivateAsync(archive.RtId);
        }

        return archive.RtId;
    }

    /// <summary>A raw archive on a DIFFERENT CK type — the target-type mismatch probe.</summary>
    private async Task<OctoObjectId> CreateForeignTypeArchiveAsync(string namePrefix)
    {
        var archive = new RtRawArchive
        {
            RtWellKnownName = $"{namePrefix}{Guid.NewGuid():N}",
            TargetCkTypeId = "AssetRepositoryIntegrationTest/Product",
            Status = RtCkArchiveStatusEnum.Created,
            Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
            {
                new() { Path = "ProductName", Indexed = true, Required = false },
            },
        };
        await InsertAsync(archive);
        return archive.RtId;
    }

    private async Task InsertAsync(RtEntity entity)
    {
        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        await repository.InsertOneRtEntityAsync(session, entity);
        await session.CommitTransactionAsync();
    }

    /// <summary>
    /// Rewrites a rollup's <c>Sources</c> straight onto the entity — the shape an <c>ImportRt</c>
    /// seed produces, bypassing every lifecycle-service validation.
    /// </summary>
    private async Task SeedSourcesAsync(OctoObjectId rollupRtId, IReadOnlyList<RollupSourceReference> sources)
    {
        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        using var session = await repository.GetSessionAsync();
        var entity = await repository.GetRtEntityByRtIdAsync<RtRollupArchive>(session, rollupRtId);
        entity.Should().NotBeNull();

        var list = new AttributeRecordValueList<RtCkRollupSourceReferenceRecord>();
        list.AddRange(sources.Select(s => new RtCkRollupSourceReferenceRecord
        {
            SourceArchiveRtId = s.SourceArchiveRtId.ToString(),
            ValidFrom = s.ValidFrom,
            ValidTo = s.ValidTo,
        }));
        entity!.Sources = list;

        await repository.UpdateOneRtEntityByIdAsync(session, rollupRtId, entity);
    }
}
