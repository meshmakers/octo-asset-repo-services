using System.Threading;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;

internal class OctoSessionAccessor : IOctoSessionAccessor
{
    private static readonly AsyncLocal<IOctoSession> _current = new();

    /// <inheritdoc />
    public IOctoSession Session
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
