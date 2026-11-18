using System.Text.Json;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Methods.Operations;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Methods;

/// <summary>
///     AB#9056: how the REST facades of <c>Recompute-2</c> and <c>BackfillFromSource-2</c> wait for their operation. They read
///     the operation record from the store of the host (the same record runtime.operations shows) - never the request's
///     session - until a condition holds.
/// </summary>
internal static class RecomputeOperationWait
{
    /// <summary>The result of a wait: the record that met the condition, or none (unknown record or timeout).</summary>
    internal readonly record struct Result(CkMethodOperationRecord? Record, bool TimedOut);

    /// <summary>The operation ended (Succeeded or Failed).</summary>
    public static bool Ended(CkMethodOperationRecord record) => record.IsFinal;

    /// <summary>The operation ended, or it reported the job it queued (progress parameter <c>jobRtId</c>).</summary>
    public static bool EndedOrJobQueued(CkMethodOperationRecord record) =>
        record.IsFinal || JobRtIdOf(record) is not null;

    /// <summary>
    ///     Reads the record every <paramref name="pollInterval" /> until <paramref name="until" /> holds. Returns no record when
    ///     the record is unknown (expired, or of another tenant) or when <paramref name="timeout" /> passed; throws
    ///     <see cref="OperationCanceledException" /> only for <paramref name="cancellationToken" />.
    /// </summary>
    public static async Task<Result> WaitAsync(ICkMethodOperationStore store, string tenantId, string operationId,
        Func<CkMethodOperationRecord, bool> until, TimeSpan pollInterval, TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(until);
        var deadline = timeout is { } t ? DateTime.UtcNow + t : (DateTime?)null;
        while (true)
        {
            var record = await store.GetAsync(tenantId, operationId, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                return new Result(null, false);
            }

            if (until(record))
            {
                return new Result(record, false);
            }

            if (deadline is { } d && DateTime.UtcNow >= d)
            {
                return new Result(null, true);
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     The job of an operation: the <c>jobRtId</c> of a declared error or of the last progress report, else the
    ///     <c>JobRtId</c> of the result record. <c>null</c> when the operation names no job (an empty source, a refusal).
    /// </summary>
    public static OctoObjectId? JobRtIdOf(CkMethodOperationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Error?.Parameters.GetValueOrDefault(ArchiveMethodErrors.JobRtIdParameter) is { } fromError)
        {
            return Parse(fromError);
        }

        if (record.State == CkMethodOperationState.Succeeded)
        {
            return Parse(JobRtIdOfResult(record.ResultJson));
        }

        return record.Progress?.Parameters.GetValueOrDefault(ArchiveMethodErrors.JobRtIdParameter) is { } fromProgress
            ? Parse(fromProgress)
            : null;
    }

    /// <summary>The <c>JobRtId</c> of a stored <c>RecomputeJobInfo</c> result (wrapped in <c>value</c> or bare).</summary>
    internal static string? JobRtIdOfResult(string? resultJson)
    {
        if (string.IsNullOrEmpty(resultJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(resultJson);
        var root = document.RootElement;
        var record = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var value) ? value : root;
        if (record.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in record.EnumerateObject())
        {
            if (string.Equals(property.Name, "jobRtId", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static OctoObjectId? Parse(string? value) =>
        value != null && OctoObjectId.TryParse(value, out var id) ? id : null;
}
