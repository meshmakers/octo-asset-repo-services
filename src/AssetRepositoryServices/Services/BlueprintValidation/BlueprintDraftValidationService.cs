using System.Text;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.ConstructionKit.Engine.BlueprintCatalogs;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.BlueprintValidation;

/// <summary>
///     Validates an uploaded blueprint draft for <c>POST {tenantId}/v1/blueprints/validate</c> (AB#9371).
/// </summary>
public interface IBlueprintDraftValidationService
{
    /// <summary>
    ///     Validates a zip of the blueprint folder or a single <c>blueprint.yaml</c>. Never throws for a broken draft:
    ///     an unreadable zip is finding 167.
    /// </summary>
    Task<BlueprintDraftValidationResponseDto> ValidateAsync(Stream upload, string? fileName,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Runs the engine's <see cref="IBlueprintDraftValidator" /> (the one the install path and octo-bpm use) with the CK
///     model dependency-range check against all CK model catalogs of the asset repository, and maps the findings.
/// </summary>
public class BlueprintDraftValidationService : IBlueprintDraftValidationService
{
    private readonly IBlueprintDraftValidator _validator;

    /// <summary>Constructor</summary>
    public BlueprintDraftValidationService(IBlueprintDraftValidator validator)
    {
        _validator = validator;
    }

    /// <inheritdoc />
    public async Task<BlueprintDraftValidationResponseDto> ValidateAsync(Stream upload, string? fileName,
        CancellationToken cancellationToken = default)
    {
        IBlueprintFileSource source;
        var isYaml = fileName != null && (fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                                          || fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase));
        if (isYaml)
        {
            using var reader = new StreamReader(upload, Encoding.UTF8);
            var content = await reader.ReadToEndAsync(cancellationToken);
            source = new InMemoryBlueprintFileSource(
                [new KeyValuePair<string, string>("blueprint.yaml", content)]);
        }
        else
        {
            try
            {
                // ZipArchive needs a seekable stream; the upload is bounded by the request size limit.
                using var buffer = new MemoryStream();
                await upload.CopyToAsync(buffer, cancellationToken);
                buffer.Position = 0;
                source = InMemoryBlueprintFileSource.FromZip(buffer);
            }
            catch (InvalidDataException ex)
            {
                return Unreadable(ex.Message);
            }
        }

        var result = await _validator.ValidateAsync(source, new BlueprintValidationOptions { CkModelCatalogs = [] },
            cancellationToken);
        return Map(result);
    }

    private static BlueprintDraftValidationResponseDto Unreadable(string detail) => new()
    {
        IsValid = false,
        TotalFindings = 1,
        Findings =
        [
            new BlueprintDraftFindingDto
            {
                Code = BlueprintDraftValidationCodes.DraftUnreadable,
                Level = nameof(MessageLevel.Error),
                Message = $"The blueprint draft cannot be read: {detail}",
                Parameters = new Dictionary<string, string> { ["detail"] = detail }
            }
        ]
    };

    /// <summary>Maps the result; errors first, bounded to <see cref="BlueprintDraftValidationResponseDto.MaxFindings" />.</summary>
    public static BlueprintDraftValidationResponseDto Map(BlueprintDraftValidationResult result)
    {
        var ordered = result.Findings
            .OrderByDescending(f => (int)f.MessageLevel)
            .ToList();
        return new BlueprintDraftValidationResponseDto
        {
            IsValid = result.IsValid,
            BlueprintId = result.BlueprintMeta?.BlueprintId?.IsEmpty == false
                ? result.BlueprintMeta.BlueprintId.FullName
                : null,
            TotalFindings = ordered.Count,
            Truncated = ordered.Count > BlueprintDraftValidationResponseDto.MaxFindings,
            Findings = ordered.Take(BlueprintDraftValidationResponseDto.MaxFindings).Select(f => new BlueprintDraftFindingDto
            {
                Code = f.MessageNumber,
                Level = f.MessageLevel.ToString(),
                File = f.Location,
                ModelId = f.Parameters.TryGetValue("modelId", out var modelId) ? modelId : null,
                Message = f.MessageText,
                Parameters = f.Parameters.ToDictionary(p => p.Key, p => p.Value)
            }).ToList()
        };
    }
}
