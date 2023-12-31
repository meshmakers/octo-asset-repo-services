using System.Reflection;
using System.Text;
using System.Text.Json;
using GraphQL.Server.Ui.Playground;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;

internal class PlaygroundPageModel
{
    private readonly string _graphQlEndPoint;
    private readonly PlaygroundOptions _options;
    private string? _playgroundCsHtml;

    public PlaygroundPageModel(string graphQlEndPoint, PlaygroundOptions options)
    {
        _graphQlEndPoint = graphQlEndPoint;
        _options = options;
    }

    public string Render()
    {
        if (_playgroundCsHtml != null)
        {
            return _playgroundCsHtml;
        }

        var assembly = typeof(PlaygroundOptions).GetTypeInfo().Assembly;
        using (var manifestResourceStream =
               assembly.GetManifestResourceStream("GraphQL.Server.Ui.Playground.Internal.playground.cshtml"))
        {
            if (manifestResourceStream == null)
            {
                throw new InvalidOperationException("Could not load embedded resource");
            }
            using (var streamReader = new StreamReader(manifestResourceStream))
            {
                var builder = new StringBuilder(streamReader.ReadToEnd())
                    .Replace("@Model.GraphQLEndPoint", _graphQlEndPoint)
                    .Replace("@Model.SubscriptionsEndPoint", _graphQlEndPoint)
                    .Replace("@Model.GraphQLConfig", JsonSerializer.Serialize(_options.GraphQLConfig))
                    .Replace("@Model.Headers", JsonSerializer.Serialize(_options.Headers))
                    .Replace("@Model.PlaygroundSettings", JsonSerializer.Serialize(_options.PlaygroundSettings));
                _playgroundCsHtml = builder.ToString();
                return Render();
            }
        }
    }
}
