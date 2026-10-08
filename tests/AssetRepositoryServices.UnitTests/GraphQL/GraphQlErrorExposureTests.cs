using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     AB#6189: GraphQL errors must not leak exception details (stack traces, inner exception text)
///     outside Development unless explicitly opted in, while message and code stay intact.
/// </summary>
public class GraphQlErrorExposureTests
{
    private const string InternalExceptionText = "internal-db-host:27017 secret detail";

    [Theory]
    [InlineData("Production", null, false)]
    [InlineData("Staging", null, false)]
    [InlineData("Development", null, true)]
    [InlineData("Production", "true", true)]
    [InlineData("Production", "false", false)]
    [InlineData("Development", "false", false)]
    [InlineData("Production", "not-a-bool", false)]
    [InlineData(null, null, false)]
    public void ShouldExposeExceptionDetails_FollowsEnvironmentAndOptIn(string? environmentName,
        string? configuredValue, bool expected)
    {
        var environment = environmentName == null ? null : CreateEnvironment(environmentName);
        var configuration = CreateConfiguration(configuredValue);

        GraphQlErrorExposure.ShouldExposeExceptionDetails(environment, configuration).Should().Be(expected);
    }

    [Fact]
    public async Task Production_ErrorResponse_HasMessageAndCode_ButNoDetailsOrStackTrace()
    {
        var error = await ExecuteFailingQueryAsync("Production", null);

        error.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        var extensions = error.GetProperty("extensions");
        extensions.GetProperty("code").GetString().Should().Be("INVALID_OPERATION");
        extensions.TryGetProperty("details", out _).Should().BeFalse();

        var raw = error.GetRawText();
        raw.Should().NotContain(InternalExceptionText);
        raw.Should().NotContain(nameof(GraphQlErrorExposureTests));
    }

    [Fact]
    public async Task Development_ErrorResponse_ExposesDetailsWithStackTrace()
    {
        var error = await ExecuteFailingQueryAsync("Development", null);

        var extensions = error.GetProperty("extensions");
        extensions.GetProperty("code").GetString().Should().Be("INVALID_OPERATION");
        var details = extensions.GetProperty("details").GetString();
        details.Should().Contain(InternalExceptionText);
        details.Should().Contain(nameof(GraphQlErrorExposureTests));
    }

    [Fact]
    public async Task Production_WithExplicitOptIn_ExposesDetails()
    {
        var error = await ExecuteFailingQueryAsync("Production", "true");

        error.GetProperty("extensions").GetProperty("details").GetString().Should().Contain(InternalExceptionText);
    }

    private static async Task<JsonElement> ExecuteFailingQueryAsync(string environmentName, string? configuredValue)
    {
        var services = new ServiceCollection();
        services.AddSingleton(CreateEnvironment(environmentName));
        services.AddSingleton(CreateConfiguration(configuredValue));
        services.AddGraphQL(b => b
            .AddSchema<FailingSchema>()
            .AddSystemTextJson()
            .AddErrorInfoProvider(GraphQlErrorExposure.Configure));

        await using var provider = services.BuildServiceProvider();
        var executer = provider.GetRequiredService<IDocumentExecuter<FailingSchema>>();
        var result = await executer.ExecuteAsync(o =>
        {
            o.Query = "{ boom }";
            o.RequestServices = provider;
        });

        var json = provider.GetRequiredService<IGraphQLTextSerializer>().Serialize(result);
        using var document = JsonDocument.Parse(json);
        var errors = document.RootElement.GetProperty("errors");
        errors.GetArrayLength().Should().Be(1);
        return errors[0].Clone();
    }

    private static IHostEnvironment CreateEnvironment(string environmentName)
    {
        var environment = A.Fake<IHostEnvironment>();
        A.CallTo(() => environment.EnvironmentName).Returns(environmentName);
        return environment;
    }

    private static IConfiguration CreateConfiguration(string? configuredValue)
    {
        var values = new Dictionary<string, string?>();
        if (configuredValue != null)
        {
            values[GraphQlErrorExposure.ConfigurationKey] = configuredValue;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static object? ThrowInternal() => throw new InvalidOperationException(InternalExceptionText);

    // ReSharper disable once ClassNeverInstantiated.Local
    private sealed class FailingSchema : Schema
    {
        public FailingSchema(IServiceProvider serviceProvider) : base(serviceProvider)
        {
            var query = new ObjectGraphType { Name = "Query" };
            query.Field<StringGraphType>("boom").Resolve(_ => ThrowInternal());
            Query = query;
        }
    }
}
