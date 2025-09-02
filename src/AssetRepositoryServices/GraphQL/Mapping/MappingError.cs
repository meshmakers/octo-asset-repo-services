namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

internal class MappingError
{
    public required string ErrorId { get; init; }

    public required string ErrorMessage { get; init; }

    public required Dictionary<string, object?> Comparision { get; init; }
}