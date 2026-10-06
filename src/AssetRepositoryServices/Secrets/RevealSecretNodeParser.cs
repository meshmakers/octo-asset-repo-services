using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Secrets;

/// <summary>
///     Finds the <c>RevealSecret@1</c> nodes of a pipeline definition (handover §7, Q5). The definition is
///     YAML or JSON; nodes are found anywhere in the tree (triggers, transformations and nested node lists such
///     as <c>ForEach</c> bodies). Parses defensively: an invalid definition yields no references.
/// </summary>
internal static class RevealSecretNodeParser
{
    /// <summary>
    ///     The node type this parser looks for.
    /// </summary>
    internal const string RevealSecretNodeType = "RevealSecret@1";

    private const int MaxDepth = 64;

    /// <summary>
    ///     Returns the secret references of every <c>RevealSecret@1</c> node in the definition.
    /// </summary>
    /// <param name="pipeline">The pipeline</param>
    /// <param name="logger">Logger for skipped definitions (debug level, never the definition itself)</param>
    internal static IReadOnlyList<RevealSecretReference> Parse(PipelineDefinitionInfo pipeline, ILogger logger)
    {
        var result = new List<RevealSecretReference>();
        if (string.IsNullOrWhiteSpace(pipeline.Definition) ||
            pipeline.Definition.IndexOf("RevealSecret", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return result;
        }

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(pipeline.Definition));
            foreach (var document in stream.Documents)
            {
                Walk(pipeline, document.RootNode, string.Empty, 0, result);
            }
        }
        catch (YamlException e)
        {
            logger.LogDebug("Pipeline {PipelineRtId}: definition is not valid YAML/JSON, skipped for secret usages ({Reason})",
                pipeline.PipelineRtId, e.GetType().Name);
            return [];
        }

        return result;
    }

    private static void Walk(PipelineDefinitionInfo pipeline, YamlNode node, string path, int depth,
        List<RevealSecretReference> result)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        switch (node)
        {
            case YamlMappingNode mapping:
                if (path.Length > 0 &&
                    string.Equals(GetScalar(mapping, "type"), RevealSecretNodeType, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(CreateReference(pipeline, mapping, path));
                }

                foreach (var (key, value) in mapping.Children)
                {
                    if (key is YamlScalarNode { Value: { Length: > 0 } name } && value is YamlMappingNode or YamlSequenceNode)
                    {
                        Walk(pipeline, value, path.Length == 0 ? name : $"{path}.{name}", depth + 1, result);
                    }
                }

                break;
            case YamlSequenceNode sequence:
                for (var i = 0; i < sequence.Children.Count; i++)
                {
                    Walk(pipeline, sequence.Children[i], $"{path}[{i}]", depth + 1, result);
                }

                break;
        }
    }

    private static RevealSecretReference CreateReference(PipelineDefinitionInfo pipeline, YamlMappingNode node,
        string path)
    {
        var rtId = GetScalar(node, "rtId");
        return new RevealSecretReference(pipeline, path,
            NullIfEmpty(GetScalar(node, "ckTypeId")),
            NullIfEmpty(rtId),
            !string.IsNullOrWhiteSpace(GetScalar(node, "rtIdPath")),
            NullIfEmpty(GetScalar(node, "attributeName")));
    }

    private static string? GetScalar(YamlMappingNode node, string key)
    {
        foreach (var (childKey, value) in node.Children)
        {
            if (childKey is YamlScalarNode scalarKey &&
                string.Equals(scalarKey.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                return (value as YamlScalarNode)?.Value;
            }
        }

        return null;
    }

    private static string? NullIfEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
