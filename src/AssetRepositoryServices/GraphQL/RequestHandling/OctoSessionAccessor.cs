using Meshmakers.Octo.Runtime.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;

internal class OctoSessionAccessor : IOctoSessionAccessor
{
    private IOctoSession? _session;

    /// <inheritdoc />
    public IOctoSession Session
    {
        get => _session ?? throw new InvalidOperationException("Octo session is not available");
        set => _session = value;
    }
}