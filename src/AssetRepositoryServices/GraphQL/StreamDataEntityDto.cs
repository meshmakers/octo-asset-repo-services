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
    /// The Well known name of the entity
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? RtWellKnownName { get; set; }

    /// <summary>
    /// The creation date time of the entity
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTime? RtCreationDateTime { get; set; }

    /// <summary>
    /// The last changed date time of the entity
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTime? RtChangedDateTime { get; set; }

    /// <summary>
    ///     Gets or sets the properties of the entity
    /// </summary>
    [JsonExtensionData]
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public IDictionary<string, object?>? Attributes { get; set; }
}