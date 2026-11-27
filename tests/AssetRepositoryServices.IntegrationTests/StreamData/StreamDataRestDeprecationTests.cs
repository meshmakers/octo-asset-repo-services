using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Meshmakers.Octo.Services.Infrastructure.Configuration.DependencyInjection;
using Meshmakers.Octo.Services.Observability;
using Meshmakers.Octo.Services.RestDeprecation;
using Meshmakers.Octo.Services.Swagger.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
///     The 16 stream-data archive endpoints that are facades of the Archive methods carry the deprecation headers of wave
///     <c>StreamDataWave4</c> on every response (also a refusal: the headers depend on the selected endpoint, not on the
///     status code), never a <c>Sunset</c> header, are counted per endpoint template and client id, and exactly these
///     endpoints carry the attribute. The endpoints that stay REST carry none. The host is the real
///     <see cref="StreamDataController" /> behind a real MVC pipeline with the service's policies and the registrations
///     Program.cs uses (<c>AddObservability()</c> for the deprecation headers and the counter,
///     <c>AddOctoApiVersioningAndDocumentation()</c> for the OpenAPI document); only the authentication handler is a
///     stand-in that authenticates nobody, so every call is refused with <c>401</c> before an action runs and no database
///     is needed. The OpenAPI document of this host is the Swagger snapshot of the deprecated operations.
/// </summary>
public sealed class StreamDataRestDeprecationTests : IAsyncDisposable
{
    private const string Wave = StreamDataRestDeprecation.Wave4;
    private const string Since = "2026-12-01";

    // Pinned literals: the test is about the wire format.
    private const string DeprecationHeader = "@1796083200";

    private const string LinkHeader =
        "<https://docs.meshmakers.cloud/docs/technologyGuide/dataAccess/streamData/archives/reference>; rel=\"deprecation\"; type=\"text/html\"";

    private const string Tenant = "deprecationtests";
    private const string Id = "65f0c0ffee65f0c0ffee65f0";

    private readonly List<WebApplication> _hosts = [];

    /// <summary>The 16 deprecated endpoints: HTTP method and path below <c>{tenant}/v1/streamdata/</c>.</summary>
    public static TheoryData<string, string> DeprecatedEndpoints => new()
    {
        { "POST", $"archives/{Id}/activate" },
        { "POST", $"archives/{Id}/disable" },
        { "POST", $"archives/{Id}/enable" },
        { "POST", $"archives/{Id}/retry" },
        { "DELETE", $"archives/{Id}" },
        { "POST", $"archives/{Id}/freeze?until=2026-01-01T00:00:00Z" },
        { "POST", $"archives/{Id}/unfreeze" },
        { "POST", $"archives/{Id}/rewind?toBucketEnd=2026-01-01T00:00:00Z" },
        { "POST", $"archives/{Id}/computed-columns?name=x&formula=Voltage&resultType=Double" },
        { "POST", $"archives/{Id}/columns?path=Current" },
        { "DELETE", $"archives/{Id}/computed-columns/x" },
        { "PUT", $"archives/{Id}/computed-columns/x?formula=Voltage" },
        { "POST", $"archives/{Id}/computed-columns/x/retry" },
        { "POST", $"archives/{Id}/computed-columns/x/repair" },
        { "POST", $"archives/{Id}/recompute?from=2026-01-01T00:00:00Z&to=2026-01-02T00:00:00Z" },
        { "POST", $"archives/{Id}/backfill-from-source" }
    };

    /// <summary>
    ///     The endpoints that stay REST: tenant-wide enable / disable, the row writes, the reads and the rollup consistency
    ///     check and repair queue.
    /// </summary>
    public static TheoryData<string, string> EndpointsThatStayRest => new()
    {
        { "POST", "enable" },
        { "POST", "disable" },
        { "POST", $"archives/{Id}/insert" },
        { "POST", $"archives/{Id}/insertTimeRange" },
        { "GET", "rollups/validate" },
        { "POST", "rollups/repair" },
        { "GET", $"rollups/repair/{Id}" },
        { "GET", $"archives/{Id}/recompute-jobs" },
        { "GET", $"archives/{Id}/rollups" },
        { "GET", $"archives/{Id}/coverage" }
    };

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.StopAsync();
            await host.DisposeAsync();
        }
    }

    /// <remarks>
    ///     The shape the clusters run: <c>since</c> is configured at the delivery of the release, the sunset date is to be
    ///     announced and stays empty, so no <c>Sunset</c> header is sent.
    /// </remarks>
    [Theory]
    [MemberData(nameof(DeprecatedEndpoints))]
    public async Task ADeprecatedEndpoint_CarriesDeprecationAndLink_ButNoSunset(string method, string path)
    {
        var host = await StartAsync(Since);

        var response = await SendAsync(host, method, path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {path} exists and is refused without a token");
        response.Headers.GetValues("Deprecation").Should().Equal(DeprecationHeader);
        response.Headers.GetValues("Link").Should().Equal(LinkHeader);
        response.Headers.Contains("Sunset").Should().BeFalse("the sunset date is to be announced and not configured");
    }

    [Theory]
    [MemberData(nameof(DeprecatedEndpoints))]
    public async Task WithoutTheWaveDate_OnlyTheLinkIsSent(string method, string path)
    {
        var host = await StartAsync(null);

        var response = await SendAsync(host, method, path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {path}");
        response.Headers.Contains("Deprecation").Should().BeFalse("no date is invented while Since is not configured");
        response.Headers.Contains("Sunset").Should().BeFalse();
        response.Headers.GetValues("Link").Should().Equal(LinkHeader);
    }

    [Theory]
    [MemberData(nameof(EndpointsThatStayRest))]
    public async Task AnEndpointThatStaysRest_CarriesNoDeprecationHeader(string method, string path)
    {
        var host = await StartAsync(Since);

        var response = await SendAsync(host, method, path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {path} exists and is refused without a token");
        response.Headers.Contains("Deprecation").Should().BeFalse(path);
        response.Headers.Contains("Sunset").Should().BeFalse(path);
        response.Headers.Contains("Link").Should().BeFalse(path);
    }

    [Fact]
    public async Task ACall_IsCounted_PerEndpointTemplateAndClientIdOnly()
    {
        using var collector = new DeprecatedCallCollector();
        var host = await StartAsync(Since);

        await SendAsync(host, "POST", $"archives/{Id}/computed-columns/x/repair");

        // The listener sees the whole process; other test classes may run in parallel.
        var measurement = collector.Snapshot()
            .Where(m => m.Tags.TryGetValue("endpoint", out var e) && e.EndsWith("computed-columns/{name}/repair"))
            .Should().NotBeEmpty().And.Subject.First();
        measurement.Tags.Keys.Should().BeEquivalentTo("endpoint", "client_id", "status_class");
        measurement.Tags["endpoint"].Should().StartWith("POST ").And.NotContain(Tenant).And.NotContain(Id);
        measurement.Tags["client_id"].Should().Be("anonymous");
        measurement.Tags["status_class"].Should().Be("4xx");
    }

    /// <summary>
    ///     The snapshot of what OpenAPI marks deprecated in this service: every endpoint of the service that carries the
    ///     attribute, its wave, its link and no fixed date.
    /// </summary>
    [Fact]
    public async Task ExactlyTheFacadeEndpoints_CarryTheAttribute_OfWave4_WithTheMethodsLink()
    {
        var host = await StartAsync(Since);

        var marked = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => (Endpoint: e, Attribute: e.Metadata.GetMetadata<OctoRestDeprecatedAttribute>()))
            .Where(e => e.Attribute != null)
            .ToList();

        marked.Should().OnlyContain(e => e.Attribute!.Wave == Wave &&
                                         e.Attribute.Link == StreamDataRestDeprecation.MethodsLink &&
                                         string.IsNullOrEmpty(e.Attribute.Successor) &&
                                         string.IsNullOrEmpty(e.Attribute.Since) &&
                                         string.IsNullOrEmpty(e.Attribute.Sunset));
        const string prefix = "/streamdata/";
        marked.Select(e =>
            {
                var template = e.Endpoint.RoutePattern.RawText!;
                return $"{string.Join(",", e.Endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods)} " +
                       template[(template.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..];
            })
            .Should().BeEquivalentTo(
                "POST archives/{archiveRtId}/activate", "POST archives/{archiveRtId}/disable",
                "POST archives/{archiveRtId}/enable", "POST archives/{archiveRtId}/retry", "DELETE archives/{archiveRtId}",
                "POST archives/{rollupRtId}/freeze", "POST archives/{rollupRtId}/unfreeze",
                "POST archives/{rollupRtId}/rewind", "POST archives/{archiveRtId}/computed-columns",
                "POST archives/{archiveRtId}/columns", "DELETE archives/{archiveRtId}/computed-columns/{name}",
                "PUT archives/{archiveRtId}/computed-columns/{name}",
                "POST archives/{archiveRtId}/computed-columns/{name}/retry",
                "POST archives/{archiveRtId}/computed-columns/{name}/repair", "POST archives/{rollupRtId}/recompute",
                "POST archives/{rollupRtId}/backfill-from-source");
    }

    /// <summary>
    ///     The Swagger snapshot: the service's OpenAPI document marks exactly the 16 operations <c>deprecated</c> with the
    ///     notice for a wave without a sunset date, and no other operation of the stream-data controller.
    /// </summary>
    [Fact]
    public async Task TheOpenApiDocument_MarksExactlyTheFacadeOperationsDeprecated_WithoutASunsetDate()
    {
        var host = await StartAsync(Since);
        using var client = host.GetTestClient();

        var json = await client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        const string prefix = "/{tenantId}/v1/streamdata/";
        var operations = JsonNode.Parse(json)!["paths"]!.AsObject()
            .Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal))
            .SelectMany(p => p.Value!.AsObject().Select(o => (Name: $"{o.Key.ToUpperInvariant()} {p.Key[prefix.Length..]}",
                Operation: o.Value!)))
            .ToList();
        operations.Should().HaveCount(26, "16 deprecated and 10 that stay REST");

        var deprecated = operations.Where(o => o.Operation["deprecated"]?.GetValue<bool>() == true).ToList();
        deprecated.Select(o => o.Name).Should().BeEquivalentTo(
            "POST archives/{archiveRtId}/activate", "POST archives/{archiveRtId}/disable",
            "POST archives/{archiveRtId}/enable", "POST archives/{archiveRtId}/retry", "DELETE archives/{archiveRtId}",
            "POST archives/{rollupRtId}/freeze", "POST archives/{rollupRtId}/unfreeze",
            "POST archives/{rollupRtId}/rewind", "POST archives/{archiveRtId}/computed-columns",
            "POST archives/{archiveRtId}/columns", "DELETE archives/{archiveRtId}/computed-columns/{name}",
            "PUT archives/{archiveRtId}/computed-columns/{name}",
            "POST archives/{archiveRtId}/computed-columns/{name}/retry",
            "POST archives/{archiveRtId}/computed-columns/{name}/repair", "POST archives/{rollupRtId}/recompute",
            "POST archives/{rollupRtId}/backfill-from-source");
        deprecated.Should().OnlyContain(o =>
            o.Operation["description"]!.GetValue<string>().EndsWith($"Deprecated, use {StreamDataRestDeprecation.MethodsLink}.",
                StringComparison.Ordinal) &&
            !o.Operation["description"]!.GetValue<string>().Contains("Sunset", StringComparison.Ordinal));
    }

    private async Task<WebApplication> StartAsync(string? since)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        // Every key is set explicitly (empty = no date), so that dates configured in the environment cannot reach a host of
        // this test.
        builder.Configuration[$"RestDeprecation:Waves:{Wave}:Since"] = since ?? string.Empty;
        builder.Configuration[$"RestDeprecation:Waves:{Wave}:Sunset"] = string.Empty;
        // The registration Program.cs uses: AddObservability() installs the deprecation headers and the counter (its startup
        // filter puts the middleware first in the pipeline, so the order of the service's other middleware does not matter).
        builder.AddObservability();
        builder.Services.AddAuthentication(NobodyHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, NobodyHandler>(NobodyHandler.SchemeName, null);
        builder.Services.AddAuthorization(o => o.AddAssetRepositoryPolicies());
        builder.Services.AddOctoTenantRoleAuthorization();
        builder.Services.AddOctoTenantIdRouteConstraint();
        builder.Services.AddControllers().AddApplicationPart(typeof(StreamDataController).Assembly);
        // The OpenAPI registration the service uses (API versioning, the Octo transformers incl. the deprecation flag).
        builder.Services
            .AddOctoApiVersioningAndDocumentation(o => o.AuthorityUrl = "https://identity.example.test/")
            .AddVersion();

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapOpenApi();
        await app.StartAsync(TestContext.Current.CancellationToken);
        _hosts.Add(app);
        return app;
    }

    private static async Task<HttpResponseMessage> SendAsync(WebApplication host, string method, string path)
    {
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/{Tenant}/v1/streamdata/{path}");
        if (method is "POST" or "PUT")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>The scheme the controller names (<c>Bearer</c>); authenticates nobody, so every call is <c>401</c>.</summary>
    private sealed class NobodyHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Bearer";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }

    /// <summary>Collects the measurements of <c>octo.rest.deprecated.calls</c> while it lives.</summary>
    private sealed class DeprecatedCallCollector : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<DeprecatedCallMeasurement> _measurements = new();

        public DeprecatedCallCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Meshmakers.Octo.RestDeprecation" &&
                    instrument.Name == "octo.rest.deprecated.calls")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var dictionary = new Dictionary<string, string>();
                foreach (var tag in tags)
                {
                    dictionary[tag.Key] = tag.Value?.ToString() ?? string.Empty;
                }

                _measurements.Enqueue(new DeprecatedCallMeasurement(value, dictionary));
            });
            _listener.Start();
        }

        public IReadOnlyList<DeprecatedCallMeasurement> Snapshot() => _measurements.ToArray();

        public void Dispose() => _listener.Dispose();
    }

    private sealed record DeprecatedCallMeasurement(long Value, IReadOnlyDictionary<string, string> Tags);
}
