using System.Runtime.CompilerServices;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;

/// <summary>
///     Built-in data permissions of the platform file system (AB#6467, decision Gerald 2026-10-09: scope plus
///     data permissions, write needs <c>FileManagement</c>). The tenant API scope alone opens the Files API
///     to every caller; a CK type that no data policy targets is unrestricted by design (backward-compatible
///     default of the engine). This baseline closes the gap fail-closed, independent of whether the tenant
///     already carries the policies of the <c>System.Identity.Bootstrap</c> blueprint:
///     <list type="bullet">
///         <item><b>read</b> (list, download, stats, zip, linked counts): role <c>FileViewer</c> or
///         <c>FileManagement</c>;</item>
///         <item><b>write and delete</b> (create, upload, replace, rename, move, delete, root creation):
///         role <c>FileManagement</c>;</item>
///         <item>a caller with neither role is denied (REST answers 403 FORBIDDEN, GraphQL reads return nothing,
///         GraphQL mutations fail with FORBIDDEN).</item>
///     </list>
///     The baseline is not a second policy source: as soon as the tenant has its own data policy that targets a
///     System.Files type (the blueprint seeds, or an administrator's custom roles and permissions), the tenant
///     policies alone decide and the baseline steps aside. System contexts (migrations, the sweep, blueprint
///     apply) are never restricted.
/// </summary>
public static class FileSystemPermissionBaseline
{
    /// <summary>Role that may read the file system.</summary>
    public const string FileViewerRole = "FileViewer";

    /// <summary>Role that may read and change the file system.</summary>
    public const string FileManagementRole = FileSystemMutationGuard.FileManagementRole;

    /// <summary>Permission id of the baseline read grant.</summary>
    public const string ReadPermissionId = "files.baseline.read";

    /// <summary>Permission id of the baseline write grant.</summary>
    public const string ManagePermissionId = "files.baseline.manage";

    private static readonly string[] TargetCkTypeIds =
    [
        FileSystemConstants.FileSystemEntityCkTypeId,
        FileSystemConstants.FolderRootCkTypeId,
        FileSystemConstants.FolderCkTypeId,
        FileSystemConstants.FileSystemItemCkTypeId
    ];

    private static readonly ConditionalWeakTable<RtDataPolicyTable, RtDataPolicyTable> Cache = new();

    /// <summary>
    ///     The baseline rules (read and write/delete).
    /// </summary>
    public static IReadOnlyList<RtDataPolicyRule> Rules { get; } =
    [
        new(ReadPermissionId, new HashSet<string>(TargetCkTypeIds, StringComparer.Ordinal),
            [RtDataAction.Read], false, false,
            new HashSet<string>([FileViewerRole, FileManagementRole], StringComparer.Ordinal)),
        new(ManagePermissionId, new HashSet<string>(TargetCkTypeIds, StringComparer.Ordinal),
            [RtDataAction.Write, RtDataAction.Delete], false, false,
            new HashSet<string>([FileManagementRole], StringComparer.Ordinal))
    ];

    /// <summary>
    ///     Returns the table with the baseline added, or the table itself when the tenant has a policy that
    ///     targets a System.Files type already.
    /// </summary>
    public static RtDataPolicyTable Apply(RtDataPolicyTable table)
    {
        if (TargetsFileSystem(table))
        {
            return table;
        }

        return Cache.GetValue(table, static t => new RtDataPolicyTable([.. t.Rules, .. Rules]));
    }

    /// <summary>
    ///     True when any rule targets a System.Files type.
    /// </summary>
    public static bool TargetsFileSystem(RtDataPolicyTable table)
    {
        return table.AllTargetCkTypeIds.Any(t => t.StartsWith(FileSystemConstants.ModelName + "/",
            StringComparison.Ordinal));
    }
}

/// <summary>
///     Adds <see cref="FileSystemPermissionBaseline" /> to the policy table of the engine's resolver — for
///     tenants that have System.Files in their CK model only, so every other tenant keeps the engine's dormant
///     (no policy, no read filter) query shapes.
/// </summary>
public sealed class FileSystemBaselineDataPermissionResolver(
    IDataPermissionResolver inner,
    ConstructionKit.Contracts.Services.ICkCacheService ckCacheService) : IDataPermissionResolver
{
    /// <inheritdoc />
    public async Task<RtDataPolicyTable> GetPolicyTableAsync(IRuntimeRepository runtimeRepository)
    {
        var table = await inner.GetPolicyTableAsync(runtimeRepository).ConfigureAwait(false);
        return ckCacheService.TryGetRtCkType(runtimeRepository.TenantId, FileSystemService.FileType, out _)
            ? FileSystemPermissionBaseline.Apply(table)
            : table;
    }

    /// <inheritdoc />
    public void Invalidate(string tenantId) => inner.Invalidate(tenantId);
}

/// <summary>
///     Registration of the file system permission baseline.
/// </summary>
public static class FileSystemPermissionBaselineServiceCollectionExtensions
{
    /// <summary>
    ///     Wraps the engine's <see cref="IDataPermissionResolver" /> registration with
    ///     <see cref="FileSystemBaselineDataPermissionResolver" /> and registers <see cref="FileSystemAccess" />.
    ///     Must run after the engine registered its resolver.
    /// </summary>
    public static IServiceCollection AddFileSystemPermissionBaseline(this IServiceCollection services)
    {
        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(IDataPermissionResolver))
                         ?? throw new InvalidOperationException(
                             "The engine's IDataPermissionResolver must be registered before the file system baseline.");
        services.Remove(descriptor);
        services.AddSingleton<IDataPermissionResolver>(sp =>
        {
            IDataPermissionResolver inner = descriptor switch
            {
                { ImplementationInstance: IDataPermissionResolver instance } => instance,
                { ImplementationFactory: not null } => (IDataPermissionResolver)descriptor.ImplementationFactory(sp),
                { ImplementationType: not null } => (IDataPermissionResolver)ActivatorUtilities.CreateInstance(sp,
                    descriptor.ImplementationType),
                _ => throw new InvalidOperationException("Unsupported IDataPermissionResolver registration.")
            };
            return new FileSystemBaselineDataPermissionResolver(inner,
                sp.GetRequiredService<ConstructionKit.Contracts.Services.ICkCacheService>());
        });
        services.TryAddSingleton<FileSystemAccess>();
        return services;
    }
}
