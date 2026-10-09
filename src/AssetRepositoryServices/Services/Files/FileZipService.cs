using System.IO.Compression;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;

/// <summary>
///     Streamed zip downloads of the platform file system (AB#6225). The selection is expanded and checked
///     against the limits before the first byte is written; the archive is then written entry by entry
///     straight into the response, with each file streamed from GridFS — nothing is buffered in memory or on
///     disk. Everything runs in the caller's session: entries hidden by data permissions are neither
///     included nor named.
/// </summary>
public class FileZipService(FileSystemService fileSystem, ILogger<FileZipService> logger)
{
    /// <summary>
    ///     One planned zip entry.
    /// </summary>
    /// <param name="Path">Path inside the archive ('/'-separated; folders end with '/')</param>
    /// <param name="File">The file, or null for a folder entry</param>
    public sealed record ZipEntryPlan(string Path, FileSystemEntry? File);

    /// <summary>
    ///     Expands the selection into zip entries and checks the limits. Top-level entries keep their name;
    ///     folders bring their content below their name; duplicate paths get " (1)", " (2)", …
    /// </summary>
    public async Task<IReadOnlyList<ZipEntryPlan>> PlanAsync(ITenantRepository repository, IOctoSession session,
        IReadOnlyList<FileSystemEntry> selection, FilesOptions limits, CancellationToken cancellationToken)
    {
        var plan = new List<ZipEntryPlan>();
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileCount = 0;
        long totalBytes = 0;

        void Add(string path, FileSystemEntry? file)
        {
            var isFolder = file == null;
            var unique = path;
            if (usedPaths.Contains(unique + (isFolder ? "/" : string.Empty)))
            {
                var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
                var name = Path.GetFileName(path);
                var prefix = string.IsNullOrEmpty(parent) ? string.Empty : parent + "/";
                unique = prefix + FileSystemNames.NextFreeName(name,
                    candidate => usedPaths.Contains(prefix + candidate + (isFolder ? "/" : string.Empty)));
            }

            usedPaths.Add(unique + (isFolder ? "/" : string.Empty));
            plan.Add(new ZipEntryPlan(isFolder ? unique + "/" : unique, file));
            if (!isFolder)
            {
                fileCount++;
                totalBytes += file!.Content?.Size ?? 0;
                if (fileCount > limits.ZipMaxFiles)
                {
                    throw FileSystemException.ZipTooManyFiles(fileCount, limits.ZipMaxFiles);
                }
            }
        }

        foreach (var entry in selection)
        {
            if (!entry.IsContainer)
            {
                Add(entry.Name, entry);
                continue;
            }

            var topName = entry.Name.Length > 0 ? entry.Name : entry.Entity.RtWellKnownName ?? "folder";
            var before = plan.Count;
            Add(topName, null);
            var top = plan[before].Path.TrimEnd('/');

            var walk = await fileSystem.WalkAsync(repository, session, entry, limits.ZipMaxFiles * 4 + 1000,
                cancellationToken).ConfigureAwait(false);
            if (walk.Truncated)
            {
                throw FileSystemException.ZipTooManyFiles(walk.Items.Count, limits.ZipMaxFiles);
            }

            // Renamed (deduplicated) parents keep their children below the new name: paths are rebuilt
            // from the walk's relative paths under the top-level name actually used.
            foreach (var descendant in walk.Items)
            {
                Add($"{top}/{descendant.RelativePath}", descendant.Entry.IsContainer ? null : descendant.Entry);
            }
        }

        if (totalBytes > limits.ZipMaxBytes)
        {
            throw FileSystemException.ZipTooLarge(totalBytes, limits.ZipMaxBytes);
        }

        return plan;
    }

    /// <summary>
    ///     Writes the planned archive into <paramref name="output" />. Files whose bytes are missing are skipped
    ///     (logged); the archive stays valid.
    /// </summary>
    public async Task WriteAsync(ITenantRepository repository, IOctoSession session,
        IReadOnlyList<ZipEntryPlan> plan, Stream output, CancellationToken cancellationToken)
    {
        await using var archive = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true,
            entryNameEncoding: null, cancellationToken).ConfigureAwait(false);
        foreach (var item in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.File == null)
            {
                archive.CreateEntry(item.Path);
                continue;
            }

            var binaryId = item.File.Content?.BinaryId;
            if (binaryId == null)
            {
                logger.LogWarning("Zip download: file '{RtId}' has no content, skipped", item.File.Id.RtId);
                continue;
            }

            using var download = await repository.DownloadLargeBinaryAsync(session, binaryId.Value)
                .ConfigureAwait(false);
            if (download.Stream == null)
            {
                logger.LogWarning("Zip download: bytes of file '{RtId}' missing, skipped", item.File.Id.RtId);
                continue;
            }

            var zipEntry = archive.CreateEntry(item.Path, CompressionLevel.Fastest);
            if (item.File.Entity.RtChangedDateTime is { } changed && changed.Year >= 1980)
            {
                zipEntry.LastWriteTime = new DateTimeOffset(DateTime.SpecifyKind(changed, DateTimeKind.Utc));
            }

            await using var entryStream = await zipEntry.OpenAsync(cancellationToken).ConfigureAwait(false);
            await download.Stream.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
        }
    }
}
