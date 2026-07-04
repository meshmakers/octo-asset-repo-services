using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Engine.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
/// Read-side blueprint resolvers. Mounted under the <c>blueprints</c> field on the tenant
/// <c>OctoQuery</c> root. Delegates to <see cref="IBlueprintCatalogManager"/> for catalog
/// discovery and <see cref="IBlueprintService"/> / <see cref="ITenantBlueprintHistory"/> /
/// <see cref="ITenantBlueprintInstallations"/> for
/// tenant-scoped state — same wiring as the REST <c>BlueprintsController</c>, just exposed
/// as typed GraphQL instead of REST DTOs.
/// </summary>
[DoNotRegister]
internal sealed class BlueprintsQuery : ObjectGraphType
{
    private readonly ILogger<BlueprintsQuery> _logger;

    public BlueprintsQuery(ILogger<BlueprintsQuery> logger)
    {
        _logger = logger;
        Name = "BlueprintsQuery";
        Description = "Blueprint catalog discovery + tenant-scoped installation and history queries.";

        Field<NonNullGraphType<BlueprintListResponseDtoType>>("list")
            .Description("Paged list of all blueprints across the configured catalogs.")
            .Argument<IntGraphType>("skip", "Number of items to skip. Defaults to 0.")
            .Argument<IntGraphType>("take", "Page size. Defaults to 20.")
            .ResolveAsync(ResolveListAsync);

        Field<NonNullGraphType<BlueprintListResponseDtoType>>("search")
            .Description("Paged blueprint search across the configured catalogs.")
            .Argument<NonNullGraphType<StringGraphType>>("query", "Search term to match against blueprint id / description.")
            .Argument<IntGraphType>("skip", "Number of items to skip. Defaults to 0.")
            .Argument<IntGraphType>("take", "Page size. Defaults to 20.")
            .ResolveAsync(ResolveSearchAsync);

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<BlueprintCatalogDtoType>>>>("catalogs")
            .Description("Configured catalog sources — local, public GitHub, private GitHub. Used by the studio's catalog filter dropdown.")
            .Resolve(ResolveCatalogs);

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<BlueprintInstallationDtoType>>>>("installations")
            .Description("Blueprints currently installed on the tenant.")
            .ResolveAsync(ResolveInstallationsAsync);

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<BlueprintHistoryItemDtoType>>>>("history")
            .Description("Append-only audit log of blueprint operations on the tenant.")
            .ResolveAsync(ResolveHistoryAsync);

        Field<BlueprintHistoryItemDtoType>("current")
            .Description("Most recent history entry, or null when no blueprint has been applied to the tenant yet.")
            .ResolveAsync(ResolveCurrentAsync);

        Field<NonNullGraphType<BlueprintUpdateInfoDtoType>>("updateInfo")
            .Description("Available updates for the tenant's installed blueprint.")
            .ResolveAsync(ResolveUpdateInfoAsync);

        Field<NonNullGraphType<BlueprintUpdatePreviewDtoType>>("previewUpdate")
            .Description("Diff a candidate update without applying it. Mode and target version come from the input.")
            .Argument<NonNullGraphType<BlueprintUpdateRequestInputType>>("input", "Update parameters.")
            .ResolveAsync(ResolvePreviewUpdateAsync);
    }

    private async Task<object?> ResolveListAsync(IResolveFieldContext<object?> ctx)
    {
        try
        {
            var skip = ctx.GetArgument<int?>("skip") ?? 0;
            var take = ctx.GetArgument<int?>("take") ?? 20;

            var catalogManager = ctx.RequestServices!.GetRequiredService<IBlueprintCatalogManager>();
            var result = await catalogManager.ListAsync(skip, take, cancellationToken: ctx.CancellationToken);

            return MapToListResponse(result.Items, result.TotalCount, skip, take);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Blueprint list failed");
            return ctx.HandleException(e);
        }
    }

    private async Task<object?> ResolveSearchAsync(IResolveFieldContext<object?> ctx)
    {
        try
        {
            var query = ctx.GetArgument<string>("query");
            var skip = ctx.GetArgument<int?>("skip") ?? 0;
            var take = ctx.GetArgument<int?>("take") ?? 20;

            var catalogManager = ctx.RequestServices!.GetRequiredService<IBlueprintCatalogManager>();
            var result = await catalogManager.SearchAsync(query, skip, take, cancellationToken: ctx.CancellationToken);

            return MapToListResponse(result.Items, result.TotalCount, skip, take);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Blueprint search failed");
            return ctx.HandleException(e);
        }
    }

    private static object ResolveCatalogs(IResolveFieldContext<object?> ctx)
    {
        var catalogManager = ctx.RequestServices!.GetRequiredService<IBlueprintCatalogManager>();
        return catalogManager.GetCatalogList()
            .Select(t => new BlueprintCatalogDto { Name = t.Item1, Description = t.Item2 })
            .ToList();
    }

    private async Task<object?> ResolveInstallationsAsync(IResolveFieldContext<object?> ctx)
    {
        try
        {
            var gql = (GraphQlUserContext)ctx.UserContext;
            var installations = ctx.RequestServices!.GetRequiredService<ITenantBlueprintInstallations>();

            var rows = await installations.GetInstalledAsync(gql.TenantId, ctx.CancellationToken);
            return rows.Select(r => new BlueprintInstallationDto
            {
                BlueprintId = r.BlueprintId.FullName,
                InstalledAt = r.InstalledAt,
                LastUpdatedAt = r.LastUpdatedAt,
                IsDependency = r.IsDependency,
                ResolvedDependencies = r.ResolvedDependencies.Select(d => d.FullName).ToList(),
                SeedDataChecksum = r.SeedDataChecksum
            }).ToList();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Blueprint installations query failed");
            return ctx.HandleException(e);
        }
    }

    private async Task<object?> ResolveHistoryAsync(IResolveFieldContext<object?> ctx)
    {
        try
        {
            var gql = (GraphQlUserContext)ctx.UserContext;
            var history = ctx.RequestServices!.GetRequiredService<ITenantBlueprintHistory>();

            var entries = await history.GetHistoryAsync(gql.TenantId, ctx.CancellationToken);
            return entries.Select(MapHistoryItem).ToList();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Blueprint history query failed");
            return ctx.HandleException(e);
        }
    }

    private async Task<object?> ResolveCurrentAsync(IResolveFieldContext<object?> ctx)
    {
        try
        {
            var gql = (GraphQlUserContext)ctx.UserContext;
            var history = ctx.RequestServices!.GetRequiredService<ITenantBlueprintHistory>();

            var current = await history.GetCurrentAsync(gql.TenantId, ctx.CancellationToken);
            return current == null ? null : MapHistoryItem(current);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Blueprint current query failed");
            return ctx.HandleException(e);
        }
    }

    private async Task<object?> ResolveUpdateInfoAsync(IResolveFieldContext<object?> ctx)
    {
        try
        {
            var gql = (GraphQlUserContext)ctx.UserContext;
            var blueprintService = ctx.RequestServices!.GetRequiredService<IBlueprintService>();

            var info = await blueprintService.GetUpdateInfoAsync(gql.TenantId, ctx.CancellationToken);
            return new BlueprintUpdateInfoDto
            {
                CurrentBlueprintId = info?.CurrentVersion.FullName,
                CurrentVersion = info?.CurrentVersion.Version.ToString(),
                RecommendedVersion = info?.RecommendedVersion?.FullName,
                HasUpdate = info?.RecommendedVersion != null,
                AvailableVersions = info?.AvailableVersions.Select(v => v.FullName).ToList() ?? []
            };
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Blueprint updateInfo query failed");
            return ctx.HandleException(e);
        }
    }

    private async Task<object?> ResolvePreviewUpdateAsync(IResolveFieldContext<object?> ctx)
    {
        try
        {
            var input = ctx.GetArgument<BlueprintUpdateRequestInputDto>("input");
            if (string.IsNullOrWhiteSpace(input.TargetVersion))
            {
                ctx.Errors.Add(new ExecutionError("TargetVersion is required") { Code = "BAD_REQUEST" });
                return null;
            }

            var gql = (GraphQlUserContext)ctx.UserContext;
            var blueprintService = ctx.RequestServices!.GetRequiredService<IBlueprintService>();

            var targetBlueprintId = new BlueprintId(input.TargetVersion);
            var preview = await blueprintService.PreviewUpdateAsync(
                gql.TenantId, targetBlueprintId, input.UpdateMode, ctx.CancellationToken);

            return new BlueprintUpdatePreviewDto
            {
                TargetVersion = input.TargetVersion,
                EntitiesToAdd = preview.EntitiesToAdd,
                EntitiesToUpdate = preview.EntitiesToUpdate,
                EntitiesToDelete = preview.EntitiesToDelete,
                Conflicts = preview.Conflicts.Select(c => new BlueprintConflictDto
                {
                    EntityId = c.EntityId,
                    Description = c.Description,
                    SuggestedResolution = c.SuggestedResolution.ToString()
                }).ToList(),
                Warnings = preview.Warnings.ToList()
            };
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Blueprint previewUpdate failed");
            return ctx.HandleException(e);
        }
    }

    private static BlueprintListResponseDto MapToListResponse(
        IEnumerable<BlueprintCatalogResultItem> items, int totalCount, int skip, int take)
    {
        return new BlueprintListResponseDto
        {
            Items = items.Select(item => new BlueprintDto
            {
                Id = item.BlueprintId.FullName,
                Name = item.BlueprintId.Name,
                Version = item.BlueprintId.Version.ToString(),
                Description = item.Description,
                CatalogName = item.CatalogName
            }).ToList(),
            TotalCount = totalCount,
            Skip = skip,
            Take = take
        };
    }

    private static BlueprintHistoryItemDto MapHistoryItem(TenantBlueprintInfo entry)
    {
        return new BlueprintHistoryItemDto
        {
            BlueprintId = entry.BlueprintId.FullName,
            AppliedAt = entry.AppliedAt,
            ApplicationMode = entry.ApplicationMode.ToString(),
            PreviousVersion = entry.PreviousVersion?.FullName,
            EntitiesCreated = entry.EntitiesCreated,
            EntitiesUpdated = entry.EntitiesUpdated,
            EntitiesDeleted = entry.EntitiesDeleted,
            SeedDataChecksum = entry.SeedDataChecksum
        };
    }
}
