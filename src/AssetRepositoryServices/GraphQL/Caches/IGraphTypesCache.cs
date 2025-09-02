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
    RtEntityDtoType GetType(CkId<CkTypeId> ckTypeId);

    /// <summary>
    ///     Returns the construction kit type input graph type for the given construction kit type id
    /// </summary>
    /// <returns></returns>
    RtEntityDtoInputType GetInputType(CkId<CkTypeId> ckTypeId);

    /// <summary>
    ///     Returns an array of known construction kit record graph types
    /// </summary>
    /// <returns></returns>
    RtRecordDtoType[] GetRecords();

    /// <summary>
    ///     Returns the construction kit record graph type for the given construction kit record id
    /// </summary>
    /// <returns></returns>
    RtRecordDtoType GetRecord(CkId<CkRecordId> ckRecordId);

    /// <summary>
    ///     Returns the construction kit record graph type for the given construction kit record id
    /// </summary>
    /// <returns></returns>
    RtRecordDtoInputType GetRecordInput(CkId<CkRecordId> ckRecordId);

    /// <summary>
    ///     Returns the construction kit enum graph type for the given construction kit enum id
    /// </summary>
    /// <returns></returns>
    RtEnumScalarType GetEnum(CkId<CkEnumId> ckEnumId);

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
}