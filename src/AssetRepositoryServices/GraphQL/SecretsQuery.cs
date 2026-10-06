using GraphQL;
using GraphQL.Types;
using GraphQL.Types.Relay.DataObjects;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Secrets;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Secrets;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using EngineSecretStorageForm = Meshmakers.Octo.Runtime.Contracts.Secrets.SecretStorageForm;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     Secrets overview of the tenant (handover §7, AB#5544): inventory, summary and pipeline usages of every
///     Secret attribute slot. Mounted as <c>secrets</c> on the root query; the root field enforces the
///     <see cref="CommonConstants.AdminPanelManagementRole" /> role (<see cref="Resolve" />). Values are never
///     returned.
/// </summary>
[DoNotRegister]
internal sealed class SecretsQuery : ObjectGraphType<SecretsRequestContext>
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 1000;

    private readonly ILogger<SecretsQuery> _logger;

    public SecretsQuery(ILogger<SecretsQuery> logger)
    {
        _logger = logger;
        Name = "SecretsQuery";
        Description = "Secrets overview of the tenant (requires the AdminPanelManagement role). Never returns values.";

        Field<NonNullGraphType<SecretInventoryConnectionGraphType>>("inventory")
            .Description("Secret slots of the tenant with storage form, key id, set-at and re-entry state.")
            .Argument<IntGraphType>("first", "Page size. Defaults to 50.", a => a.DefaultValue = DefaultPageSize)
            .Argument<StringGraphType>("after", "Cursor of the last item of the previous page.")
            .Argument<StringGraphType>("ckTypeId", "Only entities of this CK type (derived types included).")
            .Argument<ListGraphType<NonNullGraphType<SecretStorageFormGraphType>>>("forms",
                "Only slots in one of these storage forms.")
            .Argument<BooleanGraphType>("needsReEntry", "true: only re-entry tasks; false: only slots without.")
            .Argument<StringGraphType>("search",
                "Matches rtId, rtWellKnownName, display name or attribute path (never values).")
            .ResolveAsync(ResolveInventoryAsync);

        Field<NonNullGraphType<SecretInventorySummaryGraphType>>("summary")
            .Description("Counts of all secret slots per storage form.")
            .ResolveAsync(ResolveSummaryAsync);

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<SecretUsageGraphType>>>>("usages")
            .Description("RevealSecret@1 nodes that reveal (EXACT) or may reveal (BY_TYPE) the secret slot.")
            .Argument<NonNullGraphType<StringGraphType>>("ckTypeId", "CK type of the entity.")
            .Argument<NonNullGraphType<OctoObjectIdType>>("rtId", "Runtime id of the entity.")
            .Argument<NonNullGraphType<StringGraphType>>("attributePath", "camelCase path of the slot.")
            .ResolveAsync(ResolveUsagesAsync);
    }

    /// <summary>
    ///     Resolver of the root field <c>secrets</c>: the role check for the whole overview. Without
    ///     <see cref="CommonConstants.AdminPanelManagementRole" /> the field is null with error code
    ///     <c>Forbidden</c> (handover §12).
    /// </summary>
    internal static object? Resolve(IResolveFieldContext<object?> ctx)
    {
        var gql = (GraphQlUserContext)ctx.UserContext;
        if (gql.User?.IsInRole(CommonConstants.AdminPanelManagementRole) != true)
        {
            ctx.Errors.Add(new ExecutionError(
                $"The secrets overview requires the '{CommonConstants.AdminPanelManagementRole}' role.")
            {
                Code = Statics.GraphQlSecretsForbidden
            });
            return null;
        }

        return new SecretsRequestContext(Helpers.GetTenantContext(ctx.UserContext),
            ctx.RequestServices!.GetRequiredService<SecretUsageScanner>());
    }

    private async Task<object?> ResolveInventoryAsync(IResolveFieldContext<SecretsRequestContext> ctx)
    {
        try
        {
            var first = Math.Clamp(ctx.GetArgument<int?>("first") ?? DefaultPageSize, 0, MaxPageSize);
            var offset = ConnectionUtils.OffsetOrDefault(ctx.GetArgument<string?>("after"), -1) + 1;
            var forms = ctx.GetArgument<List<EngineSecretStorageForm>?>("forms");

            var inventoryService = ctx.RequestServices!.GetRequiredService<ISecretInventoryService>();
            var page = await inventoryService.ListAsync(ctx.Source.TenantContext.TenantId, new SecretInventoryQuery
            {
                CkTypeId = NullIfEmpty(ctx.GetArgument<string?>("ckTypeId")),
                Forms = forms,
                NeedsReEntry = ctx.GetArgument<bool?>("needsReEntry"),
                Search = NullIfEmpty(ctx.GetArgument<string?>("search")),
                Skip = offset,
                Take = first
            }, ctx.CancellationToken);

            var entries = page.Items.Select(item => new SecretInventoryEntry(item, ctx.Source)).ToList();
            return new SecretInventoryConnectionDto(page.TotalCount, new PageInfo
            {
                StartCursor = entries.Count > 0 ? ConnectionUtils.OffsetToCursor(offset) : null,
                EndCursor = entries.Count > 0 ? ConnectionUtils.OffsetToCursor(offset + entries.Count - 1) : null,
                HasPreviousPage = offset > 0,
                HasNextPage = offset + entries.Count < page.TotalCount
            }, entries);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Secrets inventory failed");
            return ctx.HandleException(e);
        }
    }

    private async Task<object?> ResolveSummaryAsync(IResolveFieldContext<SecretsRequestContext> ctx)
    {
        try
        {
            var inventoryService = ctx.RequestServices!.GetRequiredService<ISecretInventoryService>();
            return await inventoryService.SummarizeAsync(ctx.Source.TenantContext.TenantId, ctx.CancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Secrets summary failed");
            return ctx.HandleException(e);
        }
    }

    private async Task<object?> ResolveUsagesAsync(IResolveFieldContext<SecretsRequestContext> ctx)
    {
        try
        {
            var index = await ctx.Source.GetUsageIndexAsync(ctx.CancellationToken);
            return index.Find(ctx.GetArgument<string>("ckTypeId").Trim(),
                ctx.GetArgument<OctoObjectId>("rtId").ToString(),
                ctx.GetArgument<string>("attributePath").Trim());
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Secret usages failed");
            return ctx.HandleException(e);
        }
    }

    private static string? NullIfEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
