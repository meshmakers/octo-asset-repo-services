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

                await using var entryStream = entry.Open();
                model = await ckSerializer.DeserializeCompiledModelRootAsync(entryStream, entry.Name, operationResult);
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

        if (model == null || operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            var reasons = string.Join("; ", operationResult.Messages.Select(m => m.MessageText));
            throw new InvalidOperationException(
                $"The file '{fileName}' cannot be read as a compiled CK model{(reasons.Length > 0 ? ": " + reasons : ".")}");
        }

        return model;
    }
}
