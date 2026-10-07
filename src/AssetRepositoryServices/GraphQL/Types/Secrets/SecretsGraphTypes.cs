using GraphQL;
using GraphQL.Types;
using GraphQL.Types.Relay;
using GraphQL.Types.Relay.DataObjects;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Secrets;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using EngineSecretStorageForm = Meshmakers.Octo.Runtime.Contracts.Secrets.SecretStorageForm;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Secrets;

/// <summary>
///     One inventory slot plus the request it belongs to (for the per-request usage index of <c>usedBy</c>).
/// </summary>
internal sealed record SecretInventoryEntry(SecretInventoryItem Item, SecretsRequestContext Request);

/// <summary>
///     A page of the secrets inventory.
/// </summary>
internal sealed record SecretInventoryConnectionDto(
    int TotalCount,
    PageInfo PageInfo,
    IReadOnlyList<SecretInventoryEntry> Items);

/// <summary>
///     Number of <c>ENC_V2</c> slots protected with one key id.
/// </summary>
internal sealed record KeyIdCountDto(string KeyId, int Count);

/// <summary>
///     GraphQL enum <c>SecretStorageForm</c> (handover §7).
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class SecretStorageFormGraphType : EnumerationGraphType
{
    public SecretStorageFormGraphType()
    {
        Name = "SecretStorageForm";
        Description = "Storage form of a secret value. Never the value.";
        Add("NOT_SET", EngineSecretStorageForm.NotSet, "Null or missing.");
        Add("PLAINTEXT", EngineSecretStorageForm.Plaintext,
            "Legacy clear text still stored (before the Encrypt sweep).");
        Add("ENC_V1", EngineSecretStorageForm.EncV1, "Legacy enc:v1 (instance key).");
        Add("ENC_V2", EngineSecretStorageForm.EncV2, "Protected, key id known.");
        Add("KEY_MISSING", EngineSecretStorageForm.KeyMissing,
            "Protected, key id not in this environment's key ring.");
        Add("CORRUPT", EngineSecretStorageForm.Corrupt,
            "Stored value cannot be parsed (reads as not set, warning logged).");
    }
}

/// <summary>
///     GraphQL enum <c>SecretUsageMatch</c> (handover §7).
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class SecretUsageMatchGraphType : EnumerationGraphType
{
    public SecretUsageMatchGraphType()
    {
        Name = "SecretUsageMatch";
        Description = "How a RevealSecret@1 node refers to a secret.";
        Add("EXACT", SecretUsageMatch.Exact, "The node names the CK type, rtId and attribute of the secret.");
        Add("BY_TYPE", SecretUsageMatch.ByType,
            "The node resolves the entity at run time (rtIdPath) on the same CK type and attribute.");
    }
}

/// <summary>
///     GraphQL type <c>SecretUsage</c> (handover §7, Q5).
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class SecretUsageGraphType : ObjectGraphType<SecretUsage>
{
    public SecretUsageGraphType()
    {
        Name = "SecretUsage";
        Description = "A pipeline node (RevealSecret@1) that reveals or may reveal a secret.";

        Field<OctoObjectIdType>("dataFlowRtId").Description("Data flow of the pipeline, if any.")
            .Resolve(ctx => ctx.Source.DataFlowRtId);
        Field<StringGraphType>("dataFlowName").Description("Name of the data flow.")
            .Resolve(ctx => ctx.Source.DataFlowName);
        Field<NonNullGraphType<OctoObjectIdType>>("pipelineRtId").Description("The pipeline.")
            .Resolve(ctx => ctx.Source.PipelineRtId);
        Field<StringGraphType>("pipelineName").Description("Name of the pipeline.")
            .Resolve(ctx => ctx.Source.PipelineName);
        Field<NonNullGraphType<StringGraphType>>("nodePath")
            .Description("Position of the node in the pipeline definition, e.g. 'transformations[3]'.")
            .Resolve(ctx => ctx.Source.NodePath);
        Field<NonNullGraphType<SecretUsageMatchGraphType>>("match").Description("Exact or by type.")
            .Resolve(ctx => ctx.Source.Match);
    }
}

/// <summary>
///     GraphQL type <c>SecretInventoryItem</c> (handover §7). Never carries a value.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class SecretInventoryItemGraphType : ObjectGraphType<SecretInventoryEntry>
{
    public SecretInventoryItemGraphType()
    {
        Name = "SecretInventoryItem";
        Description = "One secret slot of an entity: storage form, key id and set-at, never the value.";

        Field<NonNullGraphType<StringGraphType>>("ckTypeId").Description("CK type of the entity.")
            .Resolve(ctx => ctx.Source.Item.CkTypeId);
        Field<NonNullGraphType<OctoObjectIdType>>("rtId").Description("Runtime id of the entity.")
            .Resolve(ctx => ctx.Source.Item.RtId);
        Field<StringGraphType>("rtWellKnownName").Description("Well-known name of the entity.")
            .Resolve(ctx => ctx.Source.Item.RtWellKnownName);
        Field<StringGraphType>("displayName").Description("Display name of the entity (display rule), may be null.")
            .Resolve(ctx => ctx.Source.Item.DisplayName);
        Field<NonNullGraphType<StringGraphType>>("attributePath")
            .Description("camelCase path of the slot; record members as 'endpoints[key=prod].token' / 'credentials.token'.")
            .Resolve(ctx => ctx.Source.Item.AttributePath);
        Field<NonNullGraphType<StringGraphType>>("attributeName")
            .Description("CK attribute name (PascalCase) of the top-level attribute.")
            .Resolve(ctx => ctx.Source.Item.AttributeName);
        Field<NonNullGraphType<BooleanGraphType>>("required")
            .Description("True when the slot's attribute is required (record member: within its record).")
            .Resolve(ctx => ctx.Source.Item.Required);
        Field<NonNullGraphType<SecretStorageFormGraphType>>("form").Description("Storage form.")
            .Resolve(ctx => ctx.Source.Item.Form);
        Field<StringGraphType>("keyId").Description("Key id (ENC_V2 / KEY_MISSING only).")
            .Resolve(ctx => ctx.Source.Item.KeyId);
        Field<UtcDateTimeGraphType>("setAt").Description("When the value was set; null for legacy / not set.")
            .Resolve(ctx => ctx.Source.Item.SetAt);
        Field<NonNullGraphType<BooleanGraphType>>("needsReEntry")
            .Description("KEY_MISSING or CORRUPT, or NOT_SET and required.")
            .Resolve(ctx => ctx.Source.Item.NeedsReEntry);
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<SecretUsageGraphType>>>>("usedBy")
            .Description("RevealSecret@1 nodes that reveal (EXACT) or may reveal (BY_TYPE) this secret.")
            .ResolveAsync(async ctx =>
            {
                var index = await ctx.Source.Request.GetUsageIndexAsync(ctx.CancellationToken);
                var item = ctx.Source.Item;
                return index.Find(item.CkTypeId, item.RtId.ToString(), item.AttributePath);
            });
    }
}

/// <summary>
///     GraphQL type <c>SecretInventoryConnection</c> (handover §7): offset-based cursors like the other
///     connections of the API.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class SecretInventoryConnectionGraphType : ObjectGraphType<SecretInventoryConnectionDto>
{
    public SecretInventoryConnectionGraphType()
    {
        Name = "SecretInventoryConnection";
        Description = "A page of the secrets inventory.";

        Field<NonNullGraphType<IntGraphType>>("totalCount").Description("Number of slots matching the filters.")
            .Resolve(ctx => ctx.Source.TotalCount);
        Field<NonNullGraphType<PageInfoType>>("pageInfo").Description("Paging information.")
            .Resolve(ctx => ctx.Source.PageInfo);
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<SecretInventoryItemGraphType>>>>("items")
            .Description("The slots of this page.")
            .Resolve(ctx => ctx.Source.Items);
    }
}

/// <summary>
///     GraphQL type <c>KeyIdCount</c>.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class KeyIdCountGraphType : ObjectGraphType<KeyIdCountDto>
{
    public KeyIdCountGraphType()
    {
        Name = "KeyIdCount";
        Description = "Number of ENC_V2 secrets protected with a key id.";
        Field<NonNullGraphType<StringGraphType>>("keyId").Description("Key id.").Resolve(ctx => ctx.Source.KeyId);
        Field<NonNullGraphType<IntGraphType>>("count").Description("Number of secrets.")
            .Resolve(ctx => ctx.Source.Count);
    }
}

/// <summary>
///     GraphQL type <c>SecretInventorySummary</c> (handover §7).
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class SecretInventorySummaryGraphType : ObjectGraphType<SecretInventorySummary>
{
    public SecretInventorySummaryGraphType()
    {
        Name = "SecretInventorySummary";
        Description = "Counts of all secret slots of the tenant per storage form.";

        Field<NonNullGraphType<IntGraphType>>("total").Resolve(ctx => ctx.Source.Total);
        Field<NonNullGraphType<IntGraphType>>("notSet").Resolve(ctx => ctx.Source.NotSet);
        Field<NonNullGraphType<IntGraphType>>("plaintext").Resolve(ctx => ctx.Source.Plaintext);
        Field<NonNullGraphType<IntGraphType>>("encV1").Resolve(ctx => ctx.Source.EncV1);
        Field<NonNullGraphType<IntGraphType>>("encV2").Resolve(ctx => ctx.Source.EncV2);
        Field<NonNullGraphType<IntGraphType>>("keyMissing").Resolve(ctx => ctx.Source.KeyMissing);
        Field<NonNullGraphType<IntGraphType>>("corrupt").Resolve(ctx => ctx.Source.Corrupt);
        Field<NonNullGraphType<IntGraphType>>("needsReEntry").Resolve(ctx => ctx.Source.NeedsReEntry);
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<KeyIdCountGraphType>>>>("encV2ByKeyId")
            .Description("ENC_V2 slots per key id.")
            .Resolve(ctx => ctx.Source.EncV2ByKeyId
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new KeyIdCountDto(pair.Key, pair.Value))
                .ToList());
    }
}
