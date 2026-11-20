using System.Collections.Concurrent;
using FakeItEasy;
using FluentAssertions;
using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     A time-out of a read-only GraphQL blueprint preview (<c>updateInfo</c>, <c>previewUpdate</c>) ends as
///     <c>ASSET1008</c> and is logged once, as the warning of the time-out helper; other failures of the previews
///     are still logged as errors.
/// </summary>
public class BlueprintsQueryTimeLimitTests
{
    private const string TenantId = "meshtest";

    private const string UpdateInfoQuery = "{ updateInfo(blueprintName: \"Starter\") { hasUpdate } }";

    private const string PreviewUpdateQuery =
        "{ previewUpdate(input: { targetVersion: \"Starter-2.0.0\" }) { __typename } }";

    private readonly IBlueprintService _blueprintService = A.Fake<IBlueprintService>();
    private readonly CapturingLoggerProvider _logs = new();

    public static TheoryData<string> Queries => new() { UpdateInfoQuery, PreviewUpdateQuery };

    private void PreviewsThrow(Exception exception)
    {
        A.CallTo(() => _blueprintService.GetUpdateInfoAsync(TenantId, A<string?>._, A<CancellationToken>._))
            .Throws(exception);
        A.CallTo(() => _blueprintService.PreviewUpdateAsync(
                TenantId, A<BlueprintId>._, A<BlueprintUpdateMode>._, A<CancellationToken>._))
            .Throws(exception);
    }

    private async Task<ExecutionResult> ExecuteAsync(string query)
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs));
        var schema = new Schema { Query = new BlueprintsQuery(loggerFactory.CreateLogger<BlueprintsQuery>()) };
        await using var services = new ServiceCollection()
            .AddSingleton(_blueprintService)
            .AddSingleton(loggerFactory)
            .BuildServiceProvider();
        var tenant = A.Fake<ITenantContext>();
        A.CallTo(() => tenant.TenantId).Returns(TenantId);

        return await new DocumentExecuter().ExecuteAsync(o =>
        {
            o.Schema = schema;
            o.Query = query;
            o.RequestServices = services;
            o.UserContext = new GraphQlUserContext(null, tenant);
            o.CancellationToken = TestContext.Current.CancellationToken;
        });
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Preview_TimeOut_AnswersAsset1008AndLogsOnce(string query)
    {
        PreviewsThrow(new RuntimeQueryTimeoutException(TenantId, "System/Entity-1", TimeSpan.FromSeconds(120),
            TimeSpan.FromMilliseconds(120_015), null));

        var result = await ExecuteAsync(query);

        // The non-null field adds GraphQL's own null error next to ASSET1008.
        result.Errors.Should().ContainSingle(e => e.Code == "ASSET1008");
        var log = _logs.Entries.Should().ContainSingle("a time-out is logged once").Subject;
        log.Level.Should().Be(LogLevel.Warning);
        log.Category.Should().Be(RuntimeQueryTimeoutError.LogCategory);
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Preview_OtherFailure_IsLoggedAsError(string query)
    {
        PreviewsThrow(new InvalidOperationException("catalog unavailable"));

        var result = await ExecuteAsync(query);

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors!.Select(e => e.Code).Should().NotContain("ASSET1008");
        _logs.Entries.Should().Contain(e =>
            e.Level == LogLevel.Error && e.Category == typeof(BlueprintsQuery).FullName);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, LogLevel Level)> _entries = new();

        public IReadOnlyList<(string Category, LogLevel Level)> Entries => _entries.ToList();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                provider._entries.Enqueue((category, logLevel));
        }
    }
}
