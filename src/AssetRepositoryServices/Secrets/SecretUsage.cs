using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Secrets;

/// <summary>
///     How a <c>RevealSecret@1</c> node refers to a secret (handover §7, Q5).
/// </summary>
internal enum SecretUsageMatch
{
    /// <summary>
    ///     The node names the CK type, the rtId and the attribute of the secret.
    /// </summary>
    Exact = 0,

    /// <summary>
    ///     The node resolves the entity at run time (<c>rtIdPath</c>) on the same CK type and attribute: it may
    ///     reveal this secret.
    /// </summary>
    ByType = 1
}

/// <summary>
///     A pipeline node that reveals (or may reveal) a secret. Never carries a value.
/// </summary>
/// <param name="DataFlowRtId">Data flow the pipeline belongs to, if any</param>
/// <param name="DataFlowName">Name of that data flow</param>
/// <param name="PipelineRtId">The pipeline</param>
/// <param name="PipelineName">Name of the pipeline</param>
/// <param name="NodePath">Position of the node in the pipeline definition, e.g. <c>transformations[3]</c></param>
/// <param name="Match">Exact or by type</param>
internal sealed record SecretUsage(
    OctoObjectId? DataFlowRtId,
    string? DataFlowName,
    OctoObjectId PipelineRtId,
    string? PipelineName,
    string NodePath,
    SecretUsageMatch Match);

/// <summary>
///     A pipeline definition with the data flow it belongs to, as read from the tenant.
/// </summary>
/// <param name="PipelineRtId">The pipeline</param>
/// <param name="PipelineName">Name of the pipeline</param>
/// <param name="DataFlowRtId">Data flow (parent) of the pipeline, if any</param>
/// <param name="DataFlowName">Name of the data flow</param>
/// <param name="Definition">The pipeline definition (YAML or JSON)</param>
internal sealed record PipelineDefinitionInfo(
    OctoObjectId PipelineRtId,
    string? PipelineName,
    OctoObjectId? DataFlowRtId,
    string? DataFlowName,
    string? Definition);

/// <summary>
///     The secret reference of one <c>RevealSecret@1</c> node.
/// </summary>
/// <param name="Pipeline">The pipeline holding the node</param>
/// <param name="NodePath">Position of the node in the definition</param>
/// <param name="CkTypeId">The node's <c>ckTypeId</c>; <c>null</c> when it uses <c>ckTypeIdPath</c> only</param>
/// <param name="RtId">The node's <c>rtId</c>, if any (wins over <c>rtIdPath</c>)</param>
/// <param name="HasRtIdPath">True when the node has an <c>rtIdPath</c></param>
/// <param name="AttributeName">The node's <c>attributeName</c> (case-insensitive, dotted through single records)</param>
internal sealed record RevealSecretReference(
    PipelineDefinitionInfo Pipeline,
    string NodePath,
    string? CkTypeId,
    string? RtId,
    bool HasRtIdPath,
    string? AttributeName);
