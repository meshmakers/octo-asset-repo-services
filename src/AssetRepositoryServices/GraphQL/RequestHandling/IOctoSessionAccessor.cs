using Meshmakers.Octo.Runtime.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;

/// <summary>
///     Accessor to the session object (async local!)
/// </summary>
public interface IOctoSessionAccessor
{
    /// <summary>
    ///     Returns the session object of OctoMesh
    /// </summary>
    IOctoSession Session { get; set; }

    /// <summary>
    ///     Returns true if there is a session available, false otherwise
    /// </summary>
    bool HasSession { get; }

    /// <summary>
    ///     Aborts the current transaction if there is a session available
    /// </summary>
    Task AbortAsync();

    /// <summary>
    /// Commits the current transaction if there is a session available
    /// </summary>
    Task CommitAsync();
}