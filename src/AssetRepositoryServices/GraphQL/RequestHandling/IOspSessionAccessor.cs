using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;

/// <summary>
///     Accessor to the session object (async local!)
/// </summary>
public interface IOctoSessionAccessor
{
    /// <summary>
    ///     Returns the session object of Octo
    /// </summary>
    IOctoSession Session { get; set; }
}
