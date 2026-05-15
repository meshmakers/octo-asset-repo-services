using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
/// Write-side blueprint resolvers. Mounted under the <c>blueprints</c> field on the tenant
/// <c>OctoMutation</c> root. Mirrors the REST <see cref="TenantApi.v1.Controllers.BlueprintsController"/>
/// surface (install / applyUpdate / uninstall / rollback) but as typed GraphQL.
///
/// Each mutation enforces the <see cref="CommonConstants.AdminPanelManagementRole"/> role at
/// field level — the AspNetCore policy on the GraphQL endpoint only verifies authentication,
/// not the specific role needed for blueprint write operations.
/// </summary>
[DoNotRegister]
internal sealed class BlueprintsMutation : ObjectGraphType
{
    private readonly ILogger<BlueprintsMutation> _logger;

    public BlueprintsMutation(ILogger<BlueprintsMutation> logger)
    {
        _logger = logger;
        Name = "BlueprintsMutation";
        Description = "Install, update, uninstall and rollback blueprints on the active tenant.";

        Field<NonNullGraphType<BlueprintApplyResultDtoType>>("install")
            .Description("Applies a blueprint to the tenant for the first time. With force=true, re-applies seed data via upsert (recovery path).")
            .Argument<NonNullGraphType<StringGraphType>>("blueprintId", "Fully-qualified blueprint id (Name-Version).")
            .Argument<BooleanGraphType>("force", "Re-apply seed data even if the same version is already recorded. Defaults to false.")
            .ResolveAsync(ResolveInstallAsync);

        Field<NonNullGraphType<BlueprintApplyResultDtoType>>("applyUpdate")
            .Description("Applies a blueprint update to the tenant. Conflict resolutions, dry-run, and pre-update backup are controlled by the input. Returns the resulting apply summary.")
            .Argument<NonNullGraphType<BlueprintUpdateRequestInputType>>("input", "Update parameters.")
            .ResolveAsync(ResolveApplyUpdateAsync);

        Field<NonNullGraphType<BlueprintUninstallResultDtoType>>("uninstall")
            .Description("Removes a blueprint from the tenant. With cascade=true, dependents are uninstalled first and orphan dependencies are auto-cleaned.")
            .Argument<NonNullGraphType<StringGraphType>>("blueprintName", "Blueprint name (without version).")
            .Argument<BooleanGraphType>("cascade", "Also uninstall dependents and orphan dependencies. Defaults to false.")
            .ResolveAsync(ResolveUninstallAsync);

        Field<NonNullGraphType<BlueprintRestoreResultDtoType>>("rollback")
            .Description("Restores the tenant from a previously-captured backup.")
            .Argument<NonNullGraphType<StringGraphType>>("backupId", "Opaque backup id from `blueprints.backups`.")
            .ResolveAsync(ResolveRollbackAsync);
    }

    private async Task<object?> ResolveInstallAsync(IResolveFieldContext<object?> ctx)
    {
        try
        {
            if (!RequireAdminPanelRole(ctx, "install")) return null;

            var blueprintIdRaw = ctx.GetArgument<string>("blueprintId");
            var force = ctx.GetArgument<bool?>("force") ?? false;
            if (string.IsNullOrWhiteSpace(blueprintIdRaw))
            {
                ctx.Errors.Add(new ExecutionError("blueprintId is required") { Code = "BAD_REQUEST" });
                return null;
            }

            BlueprintId blueprintId;
            try { blueprintId = new BlueprintId(blueprintIdRaw); }
            catch (ArgumentException e)
            {
                ctx.Errors.Add(new ExecutionError($"Invalid blueprintId: {e.Message}") { Code = "BAD_REQUEST" });
                return null;
            }

            var gql = (GraphQlUserContext)ctx.UserContext;
            var blueprintService = ctx.RequestServices!.GetRequiredService<IBlueprintService>();

            var result = await blueprintService.ApplyBlueprintAsync(
                gql.TenantId, blueprintId, force, ctx.CancellationToken);

            if (!result.IsSuccess)
            {
                var errors = result.OperationResult.Messages
                    .Where(m => m.MessageLevel == MessageLevel.Error)
                    .Select(m => m.MessageText)
                    .ToList();
                ctx.Errors.Add(new ExecutionError(string.Join(", ",
                    errors.Count > 0 ? errors : new[] { "Blueprint apply failed" }))
                {
                    Code = "OPERATION_FAILED"
                });
                return null;
            }

            return new BlueprintApplyResultDto
            {
                Success = true,
                TenantId = result.TenantId ?? gql.TenantId,
                BlueprintId = result.BlueprintId?.FullName ?? blueprintIdRaw,
                ApplicationMode = (force
                    ? BlueprintApplicationMode.ReApply
                    : BlueprintApplicationMode.Initial).ToString(),
                SeedDataFilesApplied = result.AppliedSeedDataFiles.Count,
                LoadedCkModels = result.LoadedCkModels.Select(m => m.ToString()).ToList(),
                Warnings = result.OperationResult.Messages
                    .Where(m => m.MessageLevel == MessageLevel.Warning)
                    .Select(m => m.MessageText)
                    .ToList()
            };
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Blueprint install failed");
            return ctx.HandleException(e);
        }
    }

    private async Task<object?> ResolveApplyUpdateAsync(IResolveFieldContext<object?> ctx)
    {
        try
        {
            if (!RequireAdminPanelRole(ctx, "applyUpdate")) return null;

            var input = ctx.GetArgument<BlueprintUpdateRequestInputDto>("input");
            if (string.IsNullOrWhiteSpace(input.TargetVersion))
            {
                ctx.Errors.Add(new ExecutionError("targetVersion is required") { Code = "BAD_REQUEST" });
                return null;
            }

            var gql = (GraphQlUserContext)ctx.UserContext;
            var blueprintService = ctx.RequestServices!.GetRequiredService<IBlueprintService>();

            BlueprintId targetBlueprintId;
            try { targetBlueprintId = new BlueprintId(input.TargetVersion); }
            catch (ArgumentException e)
            {
                ctx.Errors.Add(new ExecutionError($"Invalid targetVersion: {e.Message}") { Code = "BAD_REQUEST" });
                return null;
            }

            var options = new BlueprintUpdateOptions
            {
                CreateBackup = input.CreateBackup,
                DryRun = input.DryRun
            };

            if (input.ConflictResolutions is { Count: > 0 })
            {
                options.ConflictResolutions = input.ConflictResolutions.ToDictionary(
                    r => r.EntityId,
                    r => Enum.Parse<ConflictResolution>(r.Resolution, ignoreCase: true));
            }

            var result = await blueprintService.ApplyUpdateAsync(
                gql.TenantId, targetBlueprintId, input.UpdateMode, options, ctx.CancellationToken);

            if (!result.Success)
            {
                ctx.Errors.Add(new ExecutionError(string.Join(", ",
                    result.Errors.Count > 0 ? result.Errors : new[] { "Blueprint update failed" }))
                {
                    Code = "OPERATION_FAILED"
                });
                return null;
            }

            return new BlueprintApplyResultDto
            {
                Success = true,
                TenantId = gql.TenantId,
                BlueprintId = input.TargetVersion,
                ApplicationMode = input.DryRun
                    ? "DryRun"
                    : BlueprintApplicationMode.Update.ToString(),
                SeedDataFilesApplied = result.EntitiesAdded + result.EntitiesUpdated,
                LoadedCkModels = [],
                Warnings = result.Warnings.ToList()
            };
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Blueprint applyUpdate failed");
            return ctx.HandleException(e);
        }
    }

    private async Task<object?> ResolveUninstallAsync(IResolveFieldContext<object?> ctx)
    {
        try
        {
            if (!RequireAdminPanelRole(ctx, "uninstall")) return null;

            var blueprintName = ctx.GetArgument<string>("blueprintName");
            var cascade = ctx.GetArgument<bool?>("cascade") ?? false;
            if (string.IsNullOrWhiteSpace(blueprintName))
            {
                ctx.Errors.Add(new ExecutionError("blueprintName is required") { Code = "BAD_REQUEST" });
                return null;
            }

            var gql = (GraphQlUserContext)ctx.UserContext;
            var blueprintService = ctx.RequestServices!.GetRequiredService<IBlueprintService>();

            var result = await blueprintService.UninstallAsync(
                gql.TenantId, blueprintName, cascade, ctx.CancellationToken);

            return new BlueprintUninstallResultDto
            {
                Success = result.Success,
                UninstalledBlueprintId = result.UninstalledBlueprintId?.FullName,
                EntitiesDeleted = result.EntitiesDeleted,
                CascadedDependencies = result.CascadedDependencies.Select(d => d.FullName).ToList(),
                BlockingDependents = result.BlockingDependents.Select(d => d.FullName).ToList(),
                Warnings = result.Warnings.ToList()
            };
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Blueprint uninstall failed");
            return ctx.HandleException(e);
        }
    }

    private async Task<object?> ResolveRollbackAsync(IResolveFieldContext<object?> ctx)
    {
        try
        {
            if (!RequireAdminPanelRole(ctx, "rollback")) return null;

            var backupId = ctx.GetArgument<string>("backupId");
            if (string.IsNullOrWhiteSpace(backupId))
            {
                ctx.Errors.Add(new ExecutionError("backupId is required") { Code = "BAD_REQUEST" });
                return null;
            }

            var gql = (GraphQlUserContext)ctx.UserContext;
            var blueprintService = ctx.RequestServices!.GetRequiredService<IBlueprintService>();

            var result = await blueprintService.RollbackAsync(gql.TenantId, backupId, ctx.CancellationToken);
            if (!result.Success)
            {
                ctx.Errors.Add(new ExecutionError(string.Join(", ",
                    result.Errors.Count > 0 ? result.Errors : new[] { "Blueprint rollback failed" }))
                {
                    Code = "OPERATION_FAILED"
                });
                return null;
            }

            return new BlueprintRestoreResultDto
            {
                Success = result.Success,
                EntitiesRestored = result.EntitiesRestored,
                Messages = result.Warnings.ToList()
            };
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Blueprint rollback failed");
            return ctx.HandleException(e);
        }
    }

    private static bool RequireAdminPanelRole(IResolveFieldContext<object?> ctx, string operation)
    {
        var gql = (GraphQlUserContext)ctx.UserContext;
        if (gql.User?.IsInRole(CommonConstants.AdminPanelManagementRole) == true)
        {
            return true;
        }

        ctx.Errors.Add(new ExecutionError(
            $"Blueprint {operation} requires the '{CommonConstants.AdminPanelManagementRole}' role.")
        {
            Code = Statics.GraphQlForbidden,
        });
        return false;
    }
}
