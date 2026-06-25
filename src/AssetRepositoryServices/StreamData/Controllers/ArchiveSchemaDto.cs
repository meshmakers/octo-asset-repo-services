namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;

/// <summary>
/// Server-side projection of an archive's <c>ArchiveSnapshot</c> for the archive-data export/import
/// pre-flight (AB#4230 §4.2). Returned by <c>GET streamdata/archives/{archiveRtId}/schema</c> and
/// embedded verbatim into the export ZIP's <c>metadata.archive</c> block so the import job can
/// validate that source and target schemas match (concept §6). The shape mirrors §3.1 of the
/// concept.
/// </summary>
/// <param name="RtId">The archive runtime id.</param>
/// <param name="RtWellKnownName">The archive's optional well-known name.</param>
/// <param name="Kind">Storage kind: <c>raw</c> | <c>timeRange</c> | <c>rollup</c>.</param>
/// <param name="TargetCkTypeId">
/// The archive's target CK type id (== <c>ArchiveSnapshot.TargetCkTypeId</c>) — import match key #1.
/// </param>
/// <param name="Columns">Configured columns (path/indexed/required) — import match key #2.</param>
/// <param name="RollupAggregations">
/// Rollup aggregation specs; populated only when <see cref="Kind"/> == <c>rollup</c>.
/// </param>
/// <param name="PeriodMs">
/// Window period in milliseconds; populated only when <see cref="Kind"/> == <c>timeRange</c>.
/// </param>
public sealed record ArchiveSchemaDto(
    string RtId,
    string? RtWellKnownName,
    string Kind,
    string TargetCkTypeId,
    IReadOnlyList<ArchiveSchemaColumnDto> Columns,
    IReadOnlyList<ArchiveSchemaRollupAggregationDto>? RollupAggregations,
    long? PeriodMs);

/// <summary>One configured archive column, mirroring <c>CkArchiveColumnSpec</c>.</summary>
public sealed record ArchiveSchemaColumnDto(string Path, bool Indexed, bool Required);

/// <summary>One rollup aggregation spec, mirroring <c>CkRollupAggregationSpec</c>.</summary>
public sealed record ArchiveSchemaRollupAggregationDto(string SourcePath, string Function, string? TargetColumnName);
