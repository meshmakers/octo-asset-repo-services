namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;

/// <summary>
///     REST deprecation of the stream-data archive endpoints that are facades of the System.StreamData Archive methods
///     (<c>octo-common-services/docs/rest-deprecation.md</c>): the eight lifecycle and rollup control endpoints, the six column
///     and computed-column endpoints, <c>recompute</c> and <c>backfill-from-source</c>.
/// </summary>
/// <remarks>
///     <para>
///         The dates come from configuration (<c>RestDeprecation:Waves:StreamDataWave4:Since|Sunset</c>, environment
///         <c>OCTO_RestDeprecation__Waves__StreamDataWave4__Since</c>), not from code. Until <c>Since</c> is set (the delivery
///         day of the release that contains the methods), the endpoints answer the <c>Link</c> header, are <c>deprecated</c> in
///         OpenAPI and are counted in <c>octo.rest.deprecated.calls</c>, but carry no <c>Deprecation</c> header.
///     </para>
///     <para>
///         No <c>Sunset</c>: the sunset date is to be announced. The endpoints that stay REST (tenant-wide <c>enable</c> /
///         <c>disable</c>, the row writes <c>insert</c> / <c>insertTimeRange</c>, the reads and the rollup consistency check and
///         repair queue) carry no attribute; the list with the reasons is in <c>docs/stream-data-management.md</c>.
///     </para>
/// </remarks>
public static class StreamDataRestDeprecation
{
    /// <summary>The wave of the endpoints that are facades of the Archive methods.</summary>
    public const string Wave4 = "StreamDataWave4";

    /// <summary>Documentation of the replacing methods (stream data archives reference, Archive methods).</summary>
    public const string MethodsLink = "https://docs.meshmakers.cloud/docs/technologyGuide/dataAccess/streamData/archives/reference";
}
