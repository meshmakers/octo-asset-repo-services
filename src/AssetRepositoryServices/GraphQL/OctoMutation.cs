using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     Implements mutations of Octo
/// </summary>
[DoNotRegister]
internal sealed class OctoMutation : ObjectGraphType
{
    public OctoMutation(ILoggerFactory loggerFactory, IGraphTypesCache graphTypesCache)
    {
        Field("Runtime", new RtMutation(graphTypesCache))
            .Resolve(_ => new RtEntityDto());

        Field<CkMutation>("ConstructionKit")
            .Resolve(_ => new object());

        // Archive lifecycle mutations (concept §16). Tenant context is implicit; per-mutation
        // role gating is enforced by the AspNetCore policy on the GraphQL endpoint.
        Field("StreamData", new StreamDataMutation(loggerFactory.CreateLogger<StreamDataMutation>()))
            .Resolve(_ => new object());

        // Blueprint install / update / uninstall. Per-mutation field-level
        // gating on AdminPanelManagementRole is enforced inside the resolvers.
        Field("Blueprints", new BlueprintsMutation(loggerFactory.CreateLogger<BlueprintsMutation>()))
            .Resolve(_ => new object());
    }
}