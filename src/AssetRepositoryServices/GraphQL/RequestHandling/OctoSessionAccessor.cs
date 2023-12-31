using Meshmakers.Octo.Runtime.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;

internal class OctoSessionAccessor : IOctoSessionAccessor
{
    private static readonly AsyncLocal<IOctoSession?> Current = new();

    /// <inheritdoc />
    public IOctoSession Session
    {
        get => Current.Value ?? throw new InvalidOperationException("Octo session is not available");
        set => Current.Value = value;
    }
}
