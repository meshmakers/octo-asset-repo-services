using Meshmakers.Octo.Runtime.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;

internal class OctoSessionAccessor : IOctoSessionAccessor
{
    private static readonly AsyncLocal<IOctoSession?> SessionAsyncLocal = new();

    /// <inheritdoc />
    public IOctoSession Session
    {
        get
        {
            if (SessionAsyncLocal.Value == null)
            {
                throw AssetRepositoryException.SessionUnavailable();
            }

            return SessionAsyncLocal.Value;
        }
        set => SessionAsyncLocal.Value = value;
    }

    /// <inheritdoc />
    public bool HasSession => SessionAsyncLocal.Value != null;

    /// <inheritdoc />
    public async Task AbortAsync()
    {
        if (SessionAsyncLocal.Value != null)
        {
            await SessionAsyncLocal.Value.AbortTransactionAsync();
            SessionAsyncLocal.Value = null;
        }
    }

    public async Task CommitAsync()
    {
        if (SessionAsyncLocal.Value != null)
        {
            await SessionAsyncLocal.Value.CommitTransactionAsync();
            SessionAsyncLocal.Value = null;
        }
    }


}