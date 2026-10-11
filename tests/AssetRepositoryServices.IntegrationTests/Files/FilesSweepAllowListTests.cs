using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files.Migration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Files;

/// <summary>
///     AB#6473: the sweep runs only for tenants named in <c>FilesMigration:SweepTenants</c>, on top of the global
///     <c>SweepEnabled</c>. Two tenants hold legacy System.Reporting file data; the one that is not listed must be
///     byte-identical afterwards, and the state of every tenant is observable in the migration status.
/// </summary>
[Collection(FilesMigrationCollection.Name)]
public class FilesSweepAllowListTests
{
    private const string Legacy = ReportingFilesMigrationConstants.LegacyEntityCollectionName;
    private const string Associations = ReportingFilesMigrationConstants.AssociationCollectionName;
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private readonly FilesMigrationTestFixture _fixture;

    public FilesSweepAllowListTests(FilesMigrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EmptyList_SweepsNothing_AndWarnsOncePerTenantStart()
    {
        await using var world = await TwoTenants.CreateAsync(_fixture, enabled: true, listed: []);

        foreach (var tenant in new[] { TenantA, TenantB })
        {
            var result = await world.Runner.RunAtTenantStartAsync(tenant, Ct);
            result!.Outcome.Should().Be(ReportingFilesSweepOutcome.NotListed);
        }

        await world.Runner.RunStragglerSweepAsync(Ct);

        (await world.SnapshotAsync(world.A)).Should().Be(world.BeforeA, "no document of tenant A changed");
        (await world.SnapshotAsync(world.B)).Should().Be(world.BeforeB, "no document of tenant B changed");
        world.Log.Lines(LogLevel.Warning).Should().HaveCount(2, "one line per tenant start")
            .And.OnlyContain(l => l.Contains("NOT swept") && l.Contains("FilesMigration:SweepTenants"));
        world.Provider.Lookups.Should().Be(0, "a tenant that is not swept costs no database access");
        world.Log.Lines(LogLevel.Warning).Should().Contain(l => l.Contains(TenantA))
            .And.Contain(l => l.Contains(TenantB));

        var status = await world.Status.GetStatusAsync(TenantA, Ct);
        status!.Sweep.State.Should().Be(FilesMigrationSweepStates.NotListed);
        status.Sweep.SweepEnabled.Should().BeTrue();
        status.Sweep.TenantListed.Should().BeFalse();
    }

    [Fact]
    public async Task OneListed_OnlyThatTenantIsSwept_TheOtherIsByteIdentical_AndASecondRunChangesNothing()
    {
        // case-insensitive match
        await using var world = await TwoTenants.CreateAsync(_fixture, enabled: true, listed: ["TENANT-A"]);

        var a = await world.Runner.RunAtTenantStartAsync(TenantA, Ct);
        var b = await world.Runner.RunAtTenantStartAsync(TenantB, Ct);

        a!.Outcome.Should().Be(ReportingFilesSweepOutcome.Moved, string.Join("; ", a.Errors));
        b!.Outcome.Should().Be(ReportingFilesSweepOutcome.NotListed);
        (await world.SnapshotAsync(world.B)).Should().Be(world.BeforeB, "tenant B is untouched");
        (await world.CountAsync(world.A, Legacy, FilterDefinition<BsonDocument>.Empty)).Should().Be(0, "A moved");
        world.Log.Lines(LogLevel.Warning).Should().ContainSingle(l => l.Contains(TenantB) && l.Contains("NOT swept"));

        var afterFirstRun = await world.SnapshotAsync(world.A);
        var second = await world.Runner.RunAtTenantStartAsync(TenantA, Ct);
        second!.Outcome.Should().Be(ReportingFilesSweepOutcome.NothingToDo);
        (await world.SnapshotAsync(world.A)).Should().Be(afterFirstRun, "a second run for a listed tenant writes nothing");

        var statusA = await world.Status.GetStatusAsync(TenantA, Ct);
        statusA!.Sweep.State.Should().Be(FilesMigrationSweepStates.Pending, "the tenant stays on the straggler timer");
        statusA.Sweep.TenantListed.Should().BeTrue();
        statusA.Sweep.LastSweep!.EntitiesMoved.Should().BeGreaterThan(0);

        world.Tracker.SetLastFinding(TenantA, DateTime.UtcNow - TimeSpan.FromHours(25));
        await world.Runner.RunStragglerSweepAsync(Ct);
        (await world.Status.GetStatusAsync(TenantA, Ct))!.Sweep.State.Should().Be(FilesMigrationSweepStates.Done);

        var statusB = await world.Status.GetStatusAsync(TenantB, Ct);
        statusB!.Sweep.State.Should().Be(FilesMigrationSweepStates.NotListed);
        statusB.Legacy.IsZero.Should().BeFalse();
    }

    [Fact]
    public async Task FlagOff_SweepsNothing_EvenForListedTenants()
    {
        await using var world = await TwoTenants.CreateAsync(_fixture, enabled: false, listed: [TenantA, TenantB]);

        (await world.Runner.RunAtTenantStartAsync(TenantA, Ct))!.Outcome.Should().Be(ReportingFilesSweepOutcome.Disabled);
        (await world.Runner.RunAtTenantStartAsync(TenantB, Ct))!.Outcome.Should().Be(ReportingFilesSweepOutcome.Disabled);
        await world.Runner.RunStragglerSweepAsync(Ct);

        (await world.SnapshotAsync(world.A)).Should().Be(world.BeforeA);
        (await world.SnapshotAsync(world.B)).Should().Be(world.BeforeB);
        world.Log.Lines(LogLevel.Warning).Should().BeEmpty();
        world.Log.Lines(LogLevel.Information).Should().HaveCount(2).And.OnlyContain(l => l.Contains("SweepEnabled=false"));
        world.Provider.Lookups.Should().Be(0, "with the switch off no tenant is looked up and no legacy count runs");
        (await world.Status.GetStatusAsync(TenantA, Ct))!.Sweep.State.Should().Be(FilesMigrationSweepStates.Disabled);
    }

    [Fact]
    public async Task UnknownTenantId_IsWarnedAboutOnce_AndIgnored()
    {
        await using var world = await TwoTenants.CreateAsync(_fixture, enabled: true, listed: [TenantA, "ghost", "GHOST"]);

        await world.Runner.RunAtTenantStartAsync(TenantA, Ct);
        await world.Runner.RunAtTenantStartAsync(TenantB, Ct);

        world.Log.Lines(LogLevel.Warning).Where(l => l.Contains("ghost", StringComparison.OrdinalIgnoreCase))
            .Should().ContainSingle("an unknown id is reported once at startup");
        (await world.SnapshotAsync(world.B)).Should().Be(world.BeforeB);
    }

    [Fact]
    public async Task SwitchOff_RunsNoLegacyCount_AndTheMigrationStatusAnswersOnDemand()
    {
        await using var world = await TwoTenants.CreateAsync(_fixture, enabled: false, listed: [], seedB: true);

        for (var i = 0; i < 3; i++)
        {
            await world.Runner.RunAtTenantStartAsync(TenantB, Ct);
        }

        world.Provider.Lookups.Should().Be(0, "no database lookup, so no count");
        var status = await world.Status.GetStatusAsync(TenantB, Ct); // on demand
        status!.Legacy.IsZero.Should().BeFalse();
        status.Sweep.State.Should().Be(FilesMigrationSweepStates.Disabled);
    }

    [Fact]
    public void DefaultOptions_ListNoTenant()
    {
        var options = new FilesMigrationOptions { SweepEnabled = true };
        options.SweepTenants.Should().BeEmpty();
        options.IsSweepAllowed("anything").Should().BeFalse("the switch alone sweeps nothing");
        new FilesMigrationOptions { SweepTenants = ["a"] }.IsSweepAllowed("a").Should().BeFalse("the list alone sweeps nothing");
        new FilesMigrationOptions { SweepEnabled = true, SweepTenants = [" Acme "] }.IsSweepAllowed("acme").Should().BeTrue();
        new FilesMigrationOptions { SweepEnabled = true, SweepTenants = ["acme"] }.IsSweepAllowed("acme2").Should().BeFalse(
            "the match is exact, not a prefix");
    }

    [Fact]
    public void SkipLine_IsLoggedAtALevelAssetRepoLogsOnStagingAndProd()
    {
        // nlog.config defaults Meshmakers.* to Warn when OCTO_LOG_LEVEL is unset, and the Helm chart sets it only
        // on request (values.yaml logLevel: ""). A skip line at Information would never be seen there.
        var nlog = File.ReadAllText(Path.Combine(FindServiceDirectory(), "nlog.config"));
        nlog.Should().Contain("${environment:OCTO_LOG_LEVEL:whenEmpty=Warn}");
    }

    // ---------------------------------------------------------------------------------------------

    private static string FindServiceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Octo.AssetRepServices.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "src", "AssetRepositoryServices");
    }

    /// <summary>Two tenant databases with legacy file data, a runner over them and a capturing logger.</summary>
    private sealed class TwoTenants : IAsyncDisposable
    {
        public required IMongoDatabase A { get; init; }
        public required IMongoDatabase B { get; init; }
        public required MapDatabaseProvider Provider { get; init; }
        public required ReportingFilesSweepRunner Runner { get; init; }
        public required FilesMigrationStatusService Status { get; init; }
        public required ReportingFilesSweepTracker Tracker { get; init; }
        public required CapturingLogger<ReportingFilesSweepRunner> Log { get; init; }
        public string BeforeA { get; private set; } = string.Empty;
        public string BeforeB { get; private set; } = string.Empty;

        public static async Task<TwoTenants> CreateAsync(FilesMigrationTestFixture fixture, bool enabled,
            string[] listed, bool seedB = true)
        {
            var client = new MongoClient(fixture.GetConnectionString());
            var a = client.GetDatabase("sweepallowa" + Guid.NewGuid().ToString("N"));
            var b = client.GetDatabase("sweepallowb" + Guid.NewGuid().ToString("N"));
            await SeedAsync(a);
            if (seedB)
            {
                await SeedAsync(b);
            }
            else
            {
                await b.CreateCollectionAsync("unrelated", cancellationToken: Ct);
            }

            var provider = new MapDatabaseProvider(new Dictionary<string, IMongoDatabase>(StringComparer.OrdinalIgnoreCase)
            {
                [TenantA] = a, [TenantB] = b
            });
            var options = new StaticOptionsMonitor(new FilesMigrationOptions { SweepEnabled = enabled, SweepTenants = listed });
            var tracker = new ReportingFilesSweepTracker();
            var log = new CapturingLogger<ReportingFilesSweepRunner>();
            var sweep = new ReportingFilesMoveSweep(provider, options, NullLogger<ReportingFilesMoveSweep>.Instance);
            var status = new FilesMigrationStatusService(provider, tracker, options);
            var world = new TwoTenants
            {
                A = a, B = b, Tracker = tracker, Status = status, Log = log, Provider = provider,
                Runner = new ReportingFilesSweepRunner(provider, sweep, status, tracker, options, log)
            };
            world.Provider.ResetLookups(); // the snapshots do not count
            world.BeforeA = await world.SnapshotAsync(a);
            world.BeforeB = await world.SnapshotAsync(b);
            return world;
        }

        /// <summary>Legacy root, folder, item, associations in a database that is ready as a target.</summary>
        private static async Task SeedAsync(IMongoDatabase database)
        {
            var target = database.GetCollection<BsonDocument>(FileSystemConstants.EntityCollectionName);
            await target.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("ckTypeId")), cancellationToken: Ct);

            var legacy = database.GetCollection<BsonDocument>(Legacy);
            var root = ObjectId.GenerateNewId();
            var folder = ObjectId.GenerateNewId();
            var item = ObjectId.GenerateNewId();
            await legacy.InsertManyAsync(
            [
                Entity(root, ReportingFilesMigrationConstants.LegacyFolderRootCkTypeId, "root", "Root" + root),
                Entity(folder, ReportingFilesMigrationConstants.LegacyFolderCkTypeId, "Docs", null),
                Entity(item, ReportingFilesMigrationConstants.LegacyFileSystemItemCkTypeId, "a.txt", null)
            ], cancellationToken: Ct);
            await database.GetCollection<BsonDocument>(Associations).InsertManyAsync(
            [
                Association(folder, ReportingFilesMigrationConstants.LegacyFolderCkTypeId, root,
                    ReportingFilesMigrationConstants.LegacyFolderRootCkTypeId),
                Association(item, ReportingFilesMigrationConstants.LegacyFileSystemItemCkTypeId, folder,
                    ReportingFilesMigrationConstants.LegacyFolderCkTypeId)
            ], cancellationToken: Ct);
        }

        private static BsonDocument Entity(ObjectId id, string ckTypeId, string name, string? wellKnownName)
        {
            var document = new BsonDocument
            {
                { "_id", id }, { "_t", "RtEntity" }, { "attributes", new BsonDocument("name", name) },
                { "rtCreationDateTime", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                { "rtChangedDateTime", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                { "ckTypeId", ckTypeId }
            };
            if (wellKnownName != null)
            {
                document.Add("rtWellKnownName", wellKnownName);
            }

            document.Add("rtVersion", 0);
            return document;
        }

        private static BsonDocument Association(ObjectId origin, string originCkTypeId, ObjectId target,
            string targetCkTypeId) => new()
        {
            { "_id", ObjectId.GenerateNewId() }, { "attributes", new BsonDocument() },
            { "originRtId", origin }, { "originCkTypeId", originCkTypeId },
            { "targetRtId", target }, { "targetCkTypeId", targetCkTypeId },
            { "associationRoleId", "System/ParentChild" }
        };

        /// <summary>Every document of every collection, sorted, as one string.</summary>
        public async Task<string> SnapshotAsync(IMongoDatabase database)
        {
            var parts = new List<string>();
            var names = await (await database.ListCollectionNamesAsync(cancellationToken: Ct)).ToListAsync(Ct);
            foreach (var name in names.Order(StringComparer.Ordinal))
            {
                var documents = await database.GetCollection<BsonDocument>(name)
                    .Find(FilterDefinition<BsonDocument>.Empty).Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                    .ToListAsync(Ct);
                parts.Add(name + ":" + string.Join("|", documents.Select(d => d.ToJson())));
            }

            return string.Join("\n", parts);
        }

        public Task<long> CountAsync(IMongoDatabase database, string collection, FilterDefinition<BsonDocument> filter) =>
            database.GetCollection<BsonDocument>(collection).CountDocumentsAsync(filter, cancellationToken: Ct);

        public async ValueTask DisposeAsync()
        {
            await A.Client.DropDatabaseAsync(A.DatabaseNamespace.DatabaseName);
            await B.Client.DropDatabaseAsync(B.DatabaseNamespace.DatabaseName);
        }
    }

    private sealed class MapDatabaseProvider(IReadOnlyDictionary<string, IMongoDatabase> databases)
        : ITenantMongoDatabaseProvider
    {
        private int _lookups;

        public int Lookups => Volatile.Read(ref _lookups);

        public void ResetLookups() => Volatile.Write(ref _lookups, 0);

        public Task<IMongoDatabase?> TryGetDatabaseAsync(string tenantId)
        {
            Interlocked.Increment(ref _lookups);
            return Task.FromResult(databases.TryGetValue(tenantId, out var database) ? database : null);
        }
    }

    private sealed class StaticOptionsMonitor(FilesMigrationOptions value) : IOptionsMonitor<FilesMigrationOptions>
    {
        public FilesMigrationOptions CurrentValue => value;

        public FilesMigrationOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<FilesMigrationOptions, string?> listener) => null;
    }

    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IEnumerable<string> Lines(LogLevel level)
        {
            lock (_entries)
            {
                return _entries.Where(e => e.Level == level).Select(e => e.Message).ToList();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
