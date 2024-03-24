using Meshmakers.Octo.Runtime.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;

internal class OctoSessionAccessor : IOctoSessionAccessor
{
    private static readonly AsyncLocal<IOctoSession?> SessionAsyncLocal = new();

    /// <inheritdoc />
    public IOctoSession? Session
    {
        get => SessionAsyncLocal.Value;
        set => SessionAsyncLocal.Value = value;
    }
}