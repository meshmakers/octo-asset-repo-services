using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Services.Methods;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.MethodHandlers;

/// <summary>GraphQL enum <c>MethodHandlerMethodState</c>.</summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class MethodHandlerMethodStateGraphType : EnumerationGraphType
{
    public MethodHandlerMethodStateGraphType()
    {
        Name = "MethodHandlerMethodState";
        Description = "State of a CK method in the handler registry of the answering replica.";
        Add("LIVE", MethodHandlerMethodState.Live, "At least one live handler instance, or served in-process.");
        Add("NO_LIVE_HANDLER", MethodHandlerMethodState.NoLiveHandler,
            "Handler instances are known, but all are expired, withdrawn or have no queue bound.");
        Add("NOT_ANNOUNCED", MethodHandlerMethodState.NotAnnounced,
            "The system tenant defines the method with a service handler, but no instance announced it.");
    }
}

/// <summary>GraphQL enum <c>MethodHandlerTransport</c>.</summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class MethodHandlerTransportGraphType : EnumerationGraphType
{
    public MethodHandlerTransportGraphType()
    {
        Name = "MethodHandlerTransport";
        Description = "Transport a CK method is served over.";
        Add("BUS", MethodHandlerTransport.Bus, "Message bus: handler instances of other services announce themselves.");
        Add("IN_PROCESS", MethodHandlerTransport.InProcess, "Served in-process by the answering service.");
    }
}

/// <summary>GraphQL enum <c>MethodHandlerInstanceState</c>.</summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class MethodHandlerInstanceStateGraphType : EnumerationGraphType
{
    public MethodHandlerInstanceStateGraphType()
    {
        Name = "MethodHandlerInstanceState";
        Description = "State of one handler instance at the time of the query.";
        Add("LIVE", CkMethodHandlerState.Live, "Announced, not withdrawn, not expired.");
        Add("EXPIRED", CkMethodHandlerState.Expired, "The announced TTL ran out without a heartbeat.");
        Add("WITHDRAWN", CkMethodHandlerState.Withdrawn, "The instance withdrew gracefully.");
        Add("NO_QUEUE_BOUND", CkMethodHandlerState.NoQueueBound,
            "Treated as expired because the broker returned a mandatory send (no queue bound).");
    }
}

/// <summary>GraphQL enum <c>MethodHandlerSource</c>.</summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class MethodHandlerSourceGraphType : EnumerationGraphType
{
    public MethodHandlerSourceGraphType()
    {
        Name = "MethodHandlerSource";
        Description = "Where the registry row of an instance comes from.";
        Add("HEARTBEAT", CkMethodHandlerEntrySource.Announcement,
            "Received as an announcement (heartbeat or discovery answer) or withdrawal.");
        Add("SNAPSHOT", CkMethodHandlerEntrySource.Snapshot,
            "Restored from the persisted snapshot and not yet confirmed by the instance.");
    }
}

/// <summary>GraphQL type <c>MethodHandlersAnsweredBy</c>.</summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class MethodHandlersAnsweredByGraphType : ObjectGraphType<MethodHandlersAnsweredBy>
{
    public MethodHandlersAnsweredByGraphType()
    {
        Name = "MethodHandlersAnsweredBy";
        Description =
            "The gateway replica that answered. Every replica answers from its own memory; the snapshot columns show what the other replicas wrote.";

        Field<NonNullGraphType<StringGraphType>>("dispatcherInstanceId")
            .Description("Id of the dispatcher instance of the answering replica.")
            .Resolve(ctx => ctx.Source.DispatcherInstanceId);
        Field<NonNullGraphType<StringGraphType>>("serviceVersion")
            .Description("Version of the answering service.")
            .Resolve(ctx => ctx.Source.ServiceVersion);
        Field<NonNullGraphType<BooleanGraphType>>("snapshotEnabled")
            .Description("A snapshot store is present and the persisted snapshot is enabled.")
            .Resolve(ctx => ctx.Source.SnapshotEnabled);
        Field<NonNullGraphType<UtcDateTimeGraphType>>("generatedAtUtc")
            .Description("Time the states were evaluated at.")
            .Resolve(ctx => ctx.Source.GeneratedAtUtc);
        Field<UtcDateTimeGraphType>("snapshotLoadedAtUtc")
            .Description("Time of the last snapshot load, null when none ran.")
            .Resolve(ctx => ctx.Source.SnapshotLoadedAtUtc);
        Field<IntGraphType>("snapshotRowsRestored")
            .Description("Rows taken over from the snapshot by the last load, null when none ran.")
            .Resolve(ctx => ctx.Source.SnapshotRowsRestored);
        Field<NonNullGraphType<BooleanGraphType>>("snapshotLoadFailed")
            .Description("The last snapshot load failed; the registry then fills from heartbeats.")
            .Resolve(ctx => ctx.Source.SnapshotLoadFailed);
        Field<UtcDateTimeGraphType>("lastFlushUtc")
            .Description("Time of the last successful snapshot write, null when none happened yet.")
            .Resolve(ctx => ctx.Source.LastFlushUtc);
        Field<NonNullGraphType<BooleanGraphType>>("lastFlushFailed")
            .Description("The most recent snapshot write failed (cleared by the next successful write).")
            .Resolve(ctx => ctx.Source.LastFlushFailed);
    }
}

/// <summary>GraphQL type <c>MethodHandlerInstance</c>.</summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class MethodHandlerInstanceGraphType : ObjectGraphType<MethodHandlerInstance>
{
    public MethodHandlerInstanceGraphType()
    {
        Name = "MethodHandlerInstance";
        Description = "One handler instance of a CK method. Service data only.";

        Field<NonNullGraphType<StringGraphType>>("serviceName").Description("Handler service name.")
            .Resolve(ctx => ctx.Source.ServiceName);
        Field<NonNullGraphType<StringGraphType>>("instanceId").Description("Handler instance id.")
            .Resolve(ctx => ctx.Source.InstanceId);
        Field<StringGraphType>("version").Description("Informational version of the handler service.")
            .Resolve(ctx => ctx.Source.Version);
        Field<NonNullGraphType<IntGraphType>>("contractVersion")
            .Description("Highest wire contract version the instance understands.")
            .Resolve(ctx => ctx.Source.ContractVersion);
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<StringGraphType>>>>("tenants")
            .Description("Tenant ids the instance serves (\"*\" = all).")
            .Resolve(ctx => ctx.Source.Tenants);
        Field<NonNullGraphType<UtcDateTimeGraphType>>("lastSeenUtc")
            .Description("Last announcement or withdrawal received.")
            .Resolve(ctx => ctx.Source.LastSeenUtc);
        Field<NonNullGraphType<UtcDateTimeGraphType>>("expiresUtc").Description("Live until then.")
            .Resolve(ctx => ctx.Source.ExpiresUtc);
        Field<NonNullGraphType<MethodHandlerInstanceStateGraphType>>("state").Description("State of the instance.")
            .Resolve(ctx => ctx.Source.State);
        Field<NonNullGraphType<MethodHandlerSourceGraphType>>("source")
            .Description("HEARTBEAT, or SNAPSHOT for a restored row no heartbeat has confirmed yet.")
            .Resolve(ctx => ctx.Source.Source);
        Field<UtcDateTimeGraphType>("persistedLastSeenUtc")
            .Description("Last seen of the row in the shared snapshot store, null if none or the store was not readable.")
            .Resolve(ctx => ctx.Source.PersistedLastSeenUtc);
    }
}

/// <summary>GraphQL type <c>MethodHandlerMethod</c>.</summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class MethodHandlerMethodGraphType : ObjectGraphType<MethodHandlerMethod>
{
    public MethodHandlerMethodGraphType()
    {
        Name = "MethodHandlerMethod";
        Description = "A CK method with its handler instances.";

        Field<NonNullGraphType<StringGraphType>>("methodId").Description("Qualified method id.")
            .Resolve(ctx => ctx.Source.MethodId);
        Field<NonNullGraphType<MethodHandlerTransportGraphType>>("transport").Description("Transport of the method.")
            .Resolve(ctx => ctx.Source.Transport);
        Field<NonNullGraphType<MethodHandlerMethodStateGraphType>>("state").Description("State of the method.")
            .Resolve(ctx => ctx.Source.State);
        Field<NonNullGraphType<IntGraphType>>("liveInstanceCount")
            .Description("Number of live handler instances (in-process methods have no instances).")
            .Resolve(ctx => ctx.Source.LiveInstanceCount);
        Field<UtcDateTimeGraphType>("lastSeenUtc")
            .Description("Latest last-seen time of any instance, null without instances.")
            .Resolve(ctx => ctx.Source.LastSeenUtc);
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<MethodHandlerInstanceGraphType>>>>("instances")
            .Description("Every known handler instance, including expired and withdrawn ones.")
            .Resolve(ctx => ctx.Source.Instances);
    }
}
