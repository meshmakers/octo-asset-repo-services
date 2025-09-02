using System.Text;
using System.Text.Json;
using GraphQL.Server.Ui.Altair;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;

internal class AltairPageModel(string baseUri, string graphQlEndPoint, AltairOptions options)
{
    private string? _altairCsHtml;

    public string Render()
    {
        if (_altairCsHtml != null)
        {
            return _altairCsHtml;
        }

        using var manifestResourceStream = typeof(AltairPageModel).Assembly
            .GetManifestResourceStream(
                "Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling.playground.cshtml")!;
        using var streamReader = new StreamReader(manifestResourceStream);

        var headers = new Dictionary<string, object>
        {
            ["Accept"] = "application/json",
            ["Content-Type"] = "application/json"
        };

        if (options.Headers?.Count > 0)
        {
            foreach (var item in options.Headers)
            {
                headers[item.Key] = item.Value;
            }
        }

        var builder = new StringBuilder(streamReader.ReadToEnd())
            .Replace("@Model.BaseUri", StringEncode(baseUri))
            .Replace("@Model.GraphQLEndPoint", StringEncode(graphQlEndPoint))
            .Replace("@Model.SubscriptionsEndPoint", StringEncode(options.SubscriptionsEndPoint))
            .Replace("@Model.Headers", JsonSerializer.Serialize(headers))
            .Replace("@Model.SubscriptionsPayload", JsonSerializer.Serialize(options.SubscriptionsPayload))
            .Replace("@Model.Settings", JsonSerializer.Serialize(options.Settings));

        _altairCsHtml = options.PostConfigure(options, builder.ToString());
        return _altairCsHtml;
    }

    private static string StringEncode(string value)
    {
        return value
            .Replace("\\", "\\\\") // encode  \  as  \\
            .Replace("<", "\\x3C") // encode  <  as  \x3C   -- so "<!--", "<script" and "</script" are handled correctly
            .Replace("'", "\\'") // encode  '  as  \'
            .Replace("\"", "\\\"");
        // encode  "  as  \"
    }
}