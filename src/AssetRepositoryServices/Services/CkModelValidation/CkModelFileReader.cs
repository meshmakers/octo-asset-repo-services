using System.IO.Compression;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.CkModelValidation;

/// <summary>
///     Reads an uploaded compiled CK model: a single json or yaml, or a zip whose first json (else first yaml) entry is
///     the model. Shared by <c>Models/ImportCk</c> (tenant upgrade guard, AB#6966) and <c>Models/ValidateCk</c>
///     (AB#8492), so both read a file the same way.
/// </summary>
internal static class CkModelFileReader
{
    /// <summary>True when the upload is a zip by its content type (the rule of <c>Models/ImportCk</c>).</summary>
    public static bool IsZip(string? contentType) =>
        string.Equals(contentType, "application/zip", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(contentType, "application/x-zip-compressed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Reads the compiled model. Throws <see cref="InvalidOperationException" /> with a readable reason when the file is
    ///     empty, not a zip, has no model entry or cannot be read as a compiled model.
    /// </summary>
    public static async Task<CkCompiledModelRoot> ReadCompiledModelAsync(ICkSerializer ckSerializer, Stream upload,
        string fileName, bool isZip)
    {
        var operationResult = new OperationResult();
        CkCompiledModelRoot? model;
        try
        {
            if (isZip)
            {
                using var archive = new ZipArchive(upload);
                var entry = archive.Entries.FirstOrDefault(e => Path.GetExtension(e.Name).Equals(".json", StringComparison.OrdinalIgnoreCase))
                            ?? archive.Entries.FirstOrDefault(e => Path.GetExtension(e.Name).Equals(".yaml", StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    throw new InvalidOperationException("The zip archive contains no .json or .yaml CK model file.");
                }

                // Buffered: the serializer rewinds the stream after the schema validation, which a zip entry
                // stream does not support.
                using var entryBuffer = await BufferEntryAsync(entry);
                model = await ckSerializer.DeserializeCompiledModelRootAsync(entryBuffer, entry.Name, operationResult);
            }
            else
            {
                model = await ckSerializer.DeserializeCompiledModelRootAsync(upload, fileName, operationResult);
            }
        }
        catch (InvalidDataException e)
        {
            throw new InvalidOperationException($"The file '{fileName}' is not a readable CK model archive: {e.Message}");
        }
        catch (Exception e) when (e is not InvalidOperationException and not OutOfMemoryException)
        {
            // The serializer throws on a schema or parse error; that is an unreadable upload (400), not a fault (500).
            throw new InvalidOperationException(
                $"The file '{fileName}' cannot be read as a compiled CK model: {e.Message}");
        }

        if (model == null || operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            var reasons = string.Join("; ", operationResult.Messages.Select(m => m.MessageText));
            throw new InvalidOperationException(
                $"The file '{fileName}' cannot be read as a compiled CK model{(reasons.Length > 0 ? ": " + reasons : ".")}");
        }

        return model;
    }

    /// <summary>
    ///     The model ids of <b>every</b> CK model entry of a zip upload (extension <c>.json</c>, <c>.yaml</c> or
    ///     <c>.yml</c>, any casing), not only the entry this reader and bot-services pick. The import refusal of
    ///     service-managed models checks all of them, so a different entry choice in the importing job (bot-services
    ///     picks the first <c>.yaml</c> entry when the zip holds no <c>.json</c> entry) can never import an entry the
    ///     refusal did not see. A model entry that cannot be unpacked or read as a compiled model refuses the upload
    ///     (<see cref="InvalidOperationException" />, a 400; fail closed). A stream that is no readable zip yields no ids
    ///     (the regular read reports that).
    /// </summary>
    public static async Task<IReadOnlyList<CkModelId>> ReadZipEntryModelIdsAsync(ICkSerializer ckSerializer,
        byte[] upload)
    {
        var ids = new List<CkModelId>();
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(new MemoryStream(upload, writable: false));
        }
        catch (InvalidDataException)
        {
            return ids;
        }

        using (archive)
        {
            // Bounded like a source zip of ValidateCk: the scan unpacks more than one entry, so a zip bomb must not
            // reach memory. Too many entries or too many bytes refuse the upload (fail closed, 400).
            if (archive.Entries.Count > CkModelDraftValidator.MaxZipEntries)
            {
                throw new InvalidOperationException(
                    $"The zip archive has more than {CkModelDraftValidator.MaxZipEntries} entries.");
            }

            var budget = CkModelDraftValidator.MaxUnpackedBytes;
            foreach (var entry in archive.Entries.Where(e => IsModelEntry(e.Name)))
            {
                MemoryStream buffer;
                try
                {
                    buffer = await BufferEntryAsync(entry, budget);
                }
                catch (InvalidDataException e)
                {
                    // A corrupt entry is refused, not skipped (fail closed).
                    throw new InvalidOperationException($"The zip entry '{entry.Name}' cannot be unpacked: {e.Message}");
                }

                await using var ownedBuffer = buffer;
                budget -= buffer.Length;
                // Fail closed: a model entry this service cannot read is refused, not skipped, so an importer that
                // reads it differently (another engine version during a rolling update) never gets an entry the
                // check did not judge.
                CkCompiledModelRoot? model;
                try
                {
                    model = await ckSerializer.DeserializeCompiledModelRootAsync(buffer, entry.Name,
                        new OperationResult());
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    throw new InvalidOperationException(
                        $"The zip entry '{entry.Name}' cannot be read as a compiled CK model: {e.Message}");
                }

                if (model?.ModelId == null)
                {
                    throw new InvalidOperationException(
                        $"The zip entry '{entry.Name}' cannot be read as a compiled CK model.");
                }

                ids.Add(model.ModelId);
            }
        }

        return ids;
    }

    /// <summary>
    ///     Unpacks one entry into memory, at most <paramref name="maxBytes" /> bytes (counted while reading, the declared
    ///     length of an entry is not trusted).
    /// </summary>
    private static async Task<MemoryStream> BufferEntryAsync(ZipArchiveEntry entry,
        long maxBytes = CkModelDraftValidator.MaxUnpackedBytes)
    {
        await using var entryStream = entry.Open();
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await entryStream.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                await buffer.DisposeAsync();
                throw new InvalidOperationException(
                    $"The zip archive unpacks to more than {CkModelDraftValidator.MaxUnpackedBytes / (1024 * 1024)} MB.");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer;
    }

    /// <summary>
    ///     True when the bytes start with a zip local file header (<c>PK\x03\x04</c>), whatever the content type said:
    ///     the service-managed check scans such an upload as a zip too, so a content type that only the importing job
    ///     reads as zip cannot hide an entry.
    /// </summary>
    public static bool LooksLikeZip(byte[] upload) => LooksLikeZip(upload.AsSpan());

    /// <inheritdoc cref="LooksLikeZip(byte[])" />
    public static bool LooksLikeZip(ReadOnlySpan<byte> upload) =>
        upload.Length >= 4 && upload[0] == 0x50 && upload[1] == 0x4B && upload[2] == 0x03 && upload[3] == 0x04;

    private static bool IsModelEntry(string entryName)
    {
        var extension = Path.GetExtension(entryName);
        return extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".yml", StringComparison.OrdinalIgnoreCase);
    }
}
