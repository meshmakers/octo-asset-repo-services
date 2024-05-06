using System.Text.Json.Serialization;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
/// 
/// </summary>
public class StreamDataEntityDto : GraphQlDto
{
    /// <summary>
    ///  Gets or sets the timestamp of the entity
    /// </summary>
    public DateTime TimeStamp { get; set; }

    /// <summary>
    ///     Gets or sets the id of the entity
    /// </summary>
    [JsonConverter(typeof(OctoObjectIdConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public OctoObjectId RtId { get; set; }

    /// <summary>
    ///     Gets or sets the type id of the entity
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CkId<CkTypeId> CkTypeId { get; set; } = null!;

    /// <summary>
    ///     Gets or sets the properties of the entity
    /// </summary>
    [JsonExtensionData]
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public IDictionary<string, object?>? Attributes { get; set; }
}