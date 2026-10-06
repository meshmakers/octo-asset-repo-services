using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Secrets;

/// <summary>
///     The <c>RevealSecret@1</c> nodes of a tenant, matched against secret slots (handover §7, Q5). Built once
///     per request (<see cref="SecretUsageScanner" />) and queried per inventory item.
/// </summary>
internal sealed class SecretUsageIndex
{
    private readonly IReadOnlyList<RevealSecretReference> _references;

    /// <summary>
    ///     An index without references (System.Communication not installed, no pipelines).
    /// </summary>
    internal static SecretUsageIndex Empty { get; } = new([]);

    internal SecretUsageIndex(IReadOnlyList<RevealSecretReference> references)
    {
        _references = references;
    }

    /// <summary>
    ///     Number of <c>RevealSecret@1</c> nodes found.
    /// </summary>
    internal int Count => _references.Count;

    /// <summary>
    ///     Returns the nodes that reveal (<see cref="SecretUsageMatch.Exact" />) or may reveal
    ///     (<see cref="SecretUsageMatch.ByType" />) the secret slot.
    /// </summary>
    /// <param name="ckTypeId">CK type of the entity</param>
    /// <param name="rtId">Runtime id of the entity</param>
    /// <param name="attributePath">camelCase path of the slot as the inventory reports it</param>
    internal IReadOnlyList<SecretUsage> Find(string ckTypeId, string rtId, string attributePath)
    {
        var result = new List<SecretUsage>();
        foreach (var reference in _references)
        {
            var match = Match(reference, ckTypeId, rtId, attributePath);
            if (match != null)
            {
                result.Add(new SecretUsage(reference.Pipeline.DataFlowRtId, reference.Pipeline.DataFlowName,
                    reference.Pipeline.PipelineRtId, reference.Pipeline.PipelineName, reference.NodePath, match.Value));
            }
        }

        return result;
    }

    /// <summary>
    ///     Matching rule: the CK type and the attribute must be equal (attribute case-insensitive; the node's dotted
    ///     path through single records is compared with the inventory path). A node with a fixed <c>rtId</c>
    ///     matches <see cref="SecretUsageMatch.Exact" /> on the same rtId only; a node with <c>rtIdPath</c> only
    ///     matches <see cref="SecretUsageMatch.ByType" />. A node that resolves its CK type at run time
    ///     (<c>ckTypeIdPath</c> only) cannot be attributed and is skipped.
    /// </summary>
    internal static SecretUsageMatch? Match(RevealSecretReference reference, string ckTypeId, string rtId,
        string attributePath)
    {
        if (reference.CkTypeId == null || reference.AttributeName == null ||
            !CkTypeIdEquals(reference.CkTypeId, ckTypeId) ||
            !string.Equals(reference.AttributeName, attributePath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (reference.RtId != null)
        {
            return string.Equals(reference.RtId, rtId, StringComparison.OrdinalIgnoreCase)
                ? SecretUsageMatch.Exact
                : null;
        }

        return reference.HasRtIdPath ? SecretUsageMatch.ByType : null;
    }

    private static bool CkTypeIdEquals(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            // Tolerates a version suffix on either side ("System.Communication/SftpConfiguration-1").
            return new RtCkId<CkTypeId>(left).Equals(new RtCkId<CkTypeId>(right));
        }
        catch (Exception)
        {
            return false;
        }
    }
}
