namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal class MappingResult
{
    public List<MappingError> Errors { get; } = new();
}