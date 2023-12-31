using Meshmakers.Octo.Runtime.Contracts.MongoDb;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services;

public interface IOctoService
{
    ISystemContext SystemContext { get; }
}
