using System.Text.Json;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Operations;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;

/// <summary>
///     AB#8444 (F3.6): the operation handle of an accepted call of an asynchronous CK method
///     (<c>execution.mode: async</c>). The shape is the frozen wire contract of the accepted envelope
///     (<see cref="CkMethodOperationHandle" />, AB#6874): the client polls <c>runtime.operations(operationId)</c> with it.
/// </summary>
internal sealed class CkMethodOperationHandleGraphType : ObjectGraphType<CkMethodOperationHandle>
{
    public const string TypeName = "CkMethodOperationHandle";

    public CkMethodOperationHandleGraphType()
    {
        Name = TypeName;
        Description = "Handle of an accepted call of an asynchronous CK method. Read the state, progress and result " +
                      "with runtime.operations(operationId).";

        Field<NonNullGraphType<IdGraphType>>("operationId")
            .Description("Opaque id of the operation, unique in the tenant.")
            .Resolve(ctx => ctx.Source.OperationId);
        Field<UtcDateTimeGraphType>("acceptedAtUtc")
            .Description("Time (UTC) the operation was accepted.")
            .Resolve(ctx => ctx.Source.AcceptedAtUtc);
        Field<IntGraphType>("retryAfterSeconds")
            .Description("Suggested delay before the first status check, in seconds.")
            .Resolve(ctx => ctx.Source.RetryAfterSeconds);
        Field<UtcDateTimeGraphType>("expiresAtUtc")
            .Description("Time (UTC) after which the platform may forget the operation; null while it is open.")
            .Resolve(ctx => ctx.Source.ExpiresAtUtc);
    }
}

/// <summary>AB#8444: state of an operation (<see cref="CkMethodOperationState" />).</summary>
internal sealed class CkMethodOperationStateGraphType : EnumerationGraphType<CkMethodOperationState>
{
    public const string TypeName = "CkMethodOperationState";

    public CkMethodOperationStateGraphType()
    {
        Name = TypeName;
        Description = "State of an operation of an asynchronous CK method: ACCEPTED and RUNNING are open, SUCCEEDED " +
                      "and FAILED are final (a final operation never changes again).";
    }
}

/// <summary>AB#8444: one language-neutral name/value pair (progress and error parameters).</summary>
internal sealed class CkMethodOperationParameterGraphType : ObjectGraphType<KeyValuePair<string, string>>
{
    public const string TypeName = "CkMethodOperationParameter";

    public CkMethodOperationParameterGraphType()
    {
        Name = TypeName;
        Description = "A language-neutral parameter (name and string value).";
        Field<NonNullGraphType<StringGraphType>>("name").Resolve(ctx => ctx.Source.Key);
        Field<NonNullGraphType<StringGraphType>>("value").Resolve(ctx => ctx.Source.Value);
    }

    internal static IEnumerable<KeyValuePair<string, string>> Sorted(IReadOnlyDictionary<string, string>? parameters) =>
        parameters?.OrderBy(p => p.Key, StringComparer.Ordinal) ?? Enumerable.Empty<KeyValuePair<string, string>>();
}

/// <summary>AB#8444: the last progress report of an operation (<see cref="CkMethodOperationProgress" />).</summary>
internal sealed class CkMethodOperationProgressGraphType : ObjectGraphType<CkMethodOperationProgress>
{
    public const string TypeName = "CkMethodOperationProgress";

    public CkMethodOperationProgressGraphType()
    {
        Name = TypeName;
        Description = "Progress of a running operation: an optional percentage and a language-neutral stage code " +
                      "with parameters (no message text; the client renders it in its own language).";
        Field<IntGraphType>("percent")
            .Description("Done in percent (0 to 100); null when the handler cannot tell.")
            .Resolve(ctx => ctx.Source.Percent);
        Field<StringGraphType>("stageCode")
            .Description("Stable stage code (UPPER_SNAKE_CASE), e.g. COPYING_COLLECTIONS; null when not reported.")
            .Resolve(ctx => ctx.Source.StageCode);
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<CkMethodOperationParameterGraphType>>>>("parameters")
            .Description("Parameters of the stage, sorted by name.")
            .Resolve(ctx => CkMethodOperationParameterGraphType.Sorted(ctx.Source.Parameters));
    }
}

/// <summary>AB#8444: one detail entry of an operation error (<see cref="CkMethodErrorDetail" />).</summary>
internal sealed class CkMethodOperationErrorDetailGraphType : ObjectGraphType<CkMethodErrorDetail>
{
    public const string TypeName = "CkMethodOperationErrorDetail";

    public CkMethodOperationErrorDetailGraphType()
    {
        Name = TypeName;
        Description = "Detail entry of an operation error.";
        Field<NonNullGraphType<StringGraphType>>("code").Resolve(ctx => ctx.Source.Code);
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<CkMethodOperationParameterGraphType>>>>("parameters")
            .Resolve(ctx => CkMethodOperationParameterGraphType.Sorted(ctx.Source.Parameters));
    }
}

/// <summary>
///     AB#8444: the error of a failed operation (<see cref="CkMethodError" />): the same code and parameters a synchronous
///     call returns as GraphQL error extensions, here as data of the operation. A lost handler is
///     <c>METHOD_OUTCOME_UNKNOWN</c> with the parameter <c>reason = handler-lost</c>.
/// </summary>
internal sealed class CkMethodOperationErrorGraphType : ObjectGraphType<CkMethodError>
{
    public const string TypeName = "CkMethodOperationError";

    public CkMethodOperationErrorGraphType()
    {
        Name = TypeName;
        Description = "Error of a failed operation: a stable code (platform code METHOD_* or a code the method " +
                      "declares) with language-neutral parameters. METHOD_OUTCOME_UNKNOWN with reason handler-lost " +
                      "means the handler stopped before it finished; the operation is never started again.";
        Field<NonNullGraphType<StringGraphType>>("code").Resolve(ctx => ctx.Source.Code);
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<CkMethodOperationParameterGraphType>>>>("parameters")
            .Description("Parameters of the error, sorted by name.")
            .Resolve(ctx => CkMethodOperationParameterGraphType.Sorted(ctx.Source.Parameters));
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<CkMethodOperationErrorDetailGraphType>>>>("details")
            .Description("Optional detail entries (empty when there are none).")
            .Resolve(ctx => ctx.Source.Details ?? (IReadOnlyList<CkMethodErrorDetail>)[]);
    }
}

/// <summary>
///     AB#8444: one operation of an asynchronous CK method as <c>runtime.operations</c> returns it, read from the
///     durable operation record (AB#8443). Never carries the method parameters, the caller's token or a result the CK
///     model marks sensitive (the record does not hold them).
/// </summary>
internal sealed class CkMethodOperationGraphType : ObjectGraphType<CkMethodOperationRecord>
{
    public const string TypeName = "CkMethodOperation";

    public CkMethodOperationGraphType()
    {
        Name = TypeName;
        Description = "An operation of an asynchronous CK method: state, progress and the result or error.";

        Field<NonNullGraphType<IdGraphType>>("operationId")
            .Description("Opaque id of the operation (the handle).")
            .Resolve(ctx => ctx.Source.OperationId);
        Field<NonNullGraphType<StringGraphType>>("methodId")
            .Description("Qualified, model-version-less id of the method, e.g. 'System.Bot/Tenant.Dump-1'.")
            .Resolve(ctx => ctx.Source.MethodId);
        Field<NonNullGraphType<IdGraphType>>("invocationId")
            .Description("Id of the invocation that started the operation (correlation with the handler log).")
            .Resolve(ctx => ctx.Source.InvocationId.ToString("D"));
        Field<NonNullGraphType<CkMethodOperationStateGraphType>>("state")
            .Description("Current state.")
            .Resolve(ctx => ctx.Source.State);
        Field<CkMethodOperationProgressGraphType>("progress")
            .Description("Last progress report; null when the handler reported none.")
            .Resolve(ctx => ctx.Source.Progress);
        Field<SimpleScalarType>("result")
            .Description("Result of a SUCCEEDED operation of a method with a result, in the JSON form of the method " +
                         "result; always null for a result the CK model marks sensitive.")
            .Resolve(ctx => ToValue(ctx.Source.ResultJson));
        Field<CkMethodOperationErrorGraphType>("error")
            .Description("Error of a FAILED operation.")
            .Resolve(ctx => ctx.Source.Error);
        Field<NonNullGraphType<UtcDateTimeGraphType>>("acceptedAtUtc")
            .Description("Time (UTC) the operation was accepted.")
            .Resolve(ctx => ctx.Source.AcceptedAtUtc);
        Field<UtcDateTimeGraphType>("startedAtUtc")
            .Description("Time (UTC) the handler started the work.")
            .Resolve(ctx => ctx.Source.StartedAtUtc);
        Field<UtcDateTimeGraphType>("completedAtUtc")
            .Description("Time (UTC) the operation became final.")
            .Resolve(ctx => ctx.Source.CompletedAtUtc);
        Field<UtcDateTimeGraphType>("expiresAtUtc")
            .Description("Time (UTC) after which the operation is forgotten (completion plus the retention); null " +
                         "while it is open.")
            .Resolve(ctx => ctx.Source.ExpiresAtUtc);
    }

    /// <summary>The stored result JSON as a plain value tree (objects, arrays, strings, numbers, booleans).</summary>
    internal static object? ToValue(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return ToValue(document.RootElement);
    }

    private static object? ToValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(p => p.Name, p => ToValue(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(ToValue).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}

/// <summary>AB#8444: one page of <c>runtime.operations</c>.</summary>
/// <param name="Items">The operations of the page, newest accepted first.</param>
/// <param name="TotalCount">Number of operations that match the filter (all pages).</param>
internal sealed record CkMethodOperationConnection(IReadOnlyList<CkMethodOperationRecord> Items, long TotalCount);

/// <summary>AB#8444: the result type of <c>runtime.operations</c>.</summary>
internal sealed class CkMethodOperationConnectionGraphType : ObjectGraphType<CkMethodOperationConnection>
{
    public const string TypeName = "CkMethodOperationConnection";

    public CkMethodOperationConnectionGraphType()
    {
        Name = TypeName;
        Description = "A page of operations of asynchronous CK methods (runtime.operations), newest accepted first.";
        Field<NonNullGraphType<IntGraphType>>("totalCount")
            .Description("Number of operations that match the filter, ignoring skip and take.")
            .Resolve(ctx => (int)Math.Min(ctx.Source.TotalCount, int.MaxValue));
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<CkMethodOperationGraphType>>>>("items")
            .Description("The operations of the page.")
            .Resolve(ctx => ctx.Source.Items);
    }
}
