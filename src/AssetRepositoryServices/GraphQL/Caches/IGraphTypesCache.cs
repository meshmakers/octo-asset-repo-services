using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;

/// <summary>
///     Interface of the graph type cache
/// </summary>
internal interface IGraphTypesCache
{
    /// <summary>
    ///     Gets or creates a Connection Type based on the given GraphQL type
    /// </summary>
    /// <param name="graphType">The GraphQL type</param>
    /// <returns>ConnectionType that corresponds to the given GraphQL type</returns>
    DynamicConnectionType GetOrCreateConnection(IGraphType graphType);

    /// <summary>
    ///     Returns an array of known construction kit type graph types
    /// </summary>
    /// <returns></returns>
    RtEntityDtoType[] GetTypes();

    /// <summary>
    ///     Returns the construction kit type graph type for the given construction kit type id
    /// </summary>
    /// <returns></returns>
    RtEntityDtoType GetType(RtCkId<CkTypeId> ckTypeId);

    /// <summary>
    ///     Returns the interface types that a given type implements (based on its type hierarchy).
    /// </summary>
    /// <param name="ckTypeId">The construction kit type id</param>
    /// <returns>List of interface types the type implements</returns>
    IReadOnlyList<RtEntityInterfaceType> GetImplementedInterfaces(RtCkId<CkTypeId> ckTypeId);

    /// <summary>
    ///     Returns the construction kit type input graph type for the given construction kit type id
    /// </summary>
    /// <returns></returns>
    RtEntityDtoInputType GetInputType(RtCkId<CkTypeId> ckTypeId);

    /// <summary>
    ///     Returns an array of known construction kit record graph types
    /// </summary>
    /// <returns></returns>
    RtRecordDtoType[] GetRecords();

    /// <summary>
    ///     Returns the construction kit record graph type for the given construction kit record id
    /// </summary>
    /// <returns></returns>
    RtRecordDtoType GetRecord(RtCkId<CkRecordId> ckRecordId);

    /// <summary>
    ///     Returns the construction kit record graph type for the given construction kit record id
    /// </summary>
    /// <returns></returns>
    RtRecordDtoInputType GetRecordInput(RtCkId<CkRecordId> ckRecordId);

    /// <summary>
    ///     Returns the construction kit enum graph type for the given construction kit enum id
    /// </summary>
    /// <returns></returns>
    RtEnumScalarType GetEnum(RtCkId<CkEnumId> ckEnumId);

    /// <summary>
    ///     Returns an array of known graph types
    /// </summary>
    /// <returns></returns>
    IGraphType[] GetKnownGraphTypes();

    /// <summary>
    ///     Returns an array of known stream data graph types
    /// </summary>
    /// <returns></returns>
    StreamDataEntityDtoType[] GetStreamTypes();

    /// <summary>
    ///     Gets or creates a connection type for an interface association field.
    ///     This ensures that implementing types use the same connection type as the interface.
    /// </summary>
    /// <param name="baseCkTypeId">The CK type ID of the base type where the association is defined</param>
    /// <param name="navigationPropertyName">The name of the navigation property</param>
    /// <param name="allowedTypes">The allowed types for this association - used as part of the cache key</param>
    /// <param name="factory">Factory function to create the connection type if not cached</param>
    /// <returns>The cached or newly created connection type</returns>
    DynamicConnectionType GetOrCreateInterfaceAssociationConnection(
        RtCkId<CkTypeId> baseCkTypeId,
        string navigationPropertyName,
        IReadOnlyList<RtCkId<CkTypeId>> allowedTypes,
        Func<DynamicConnectionType> factory);

    /// <summary>
    ///     Tries to get a cached interface association connection type.
    ///     Only returns a cached connection if the allowedTypes match.
    /// </summary>
    /// <param name="baseCkTypeId">The CK type ID of the base type where the association is defined</param>
    /// <param name="navigationPropertyName">The name of the navigation property</param>
    /// <param name="allowedTypes">The allowed types for this association - must match the cached types</param>
    /// <param name="connectionType">The cached connection type if found and types match</param>
    /// <returns>True if found and types match, false otherwise</returns>
    bool TryGetInterfaceAssociationConnection(
        RtCkId<CkTypeId> baseCkTypeId,
        string navigationPropertyName,
        IReadOnlyList<RtCkId<CkTypeId>> allowedTypes,
        out DynamicConnectionType? connectionType);
}