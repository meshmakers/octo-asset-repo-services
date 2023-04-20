using Meshmakers.Octo.SystematizedData.Persistence;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services;

public class OctoService : IOctoService
{
    public OctoService(ISystemContext systemContext)
    {
        SystemContext = systemContext;
    }

    public ISystemContext SystemContext { get; }
}
