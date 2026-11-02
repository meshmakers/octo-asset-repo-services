using System.Security.Claims;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     AB#7812: fixture of the anchor-column tests. Raw, time-range and rollup archives of the type
///     <c>MeteringPoint</c> whose rows point at real metering-point entities (the anchors), plus an archive of the base
///     type <c>Asset</c> whose rows point at a metering point and at an operating facility. A data policy protects the
///     facility (Read for <see cref="FacilityReaderRole" /> only), which is how a caller can lose an anchor while the
///     stream-data type gate of the archive's own type still passes (the gate looks at the archive's type, not at the
///     type of each anchor).
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class AnchorColumnsStreamDataFixture : StreamDataFixture
{
    public const string MeteringPointType = "AssetRepositoryIntegrationTest/MeteringPoint";
    public const string FacilityType = "AssetRepositoryIntegrationTest/OperatingFacility";
    public const string AssetType = "AssetRepositoryIntegrationTest/Asset";
    public const string AccountType = "AssetRepositoryIntegrationTest/AccessTestAccount";
    public const string CredentialType = "AssetRepositoryIntegrationTest/ServiceCredential";
    private const string CredentialEntryRecord = "AssetRepositoryIntegrationTest/CredentialEntry";

    /// <summary>Distinctive values of the protected attributes; no response may ever contain one of them.</summary>
    public const string CanaryPasswordHash = "CANARY-HIDDEN-HASH-7812";
    public const string CanaryPassword = "CANARY-SECRET-PASSWORD-7812";
    public const string CanaryApiKey = "CANARY-SECRET-APIKEY-7812";
    public const string CanaryToken = "CANARY-SECRET-TOKEN-7812";
    public const string CanaryEndpointToken = "CANARY-SECRET-ENDPOINT-TOKEN-7812";

    public static readonly string[] Canaries =
        [CanaryPasswordHash, CanaryPassword, CanaryApiKey, CanaryToken, CanaryEndpointToken];

    public const string FacilityReaderRole = "AnchorFacilityReader";
    public const string PlainRole = "AnchorPlain";

    public static readonly DateTime Start = new(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);
    public static readonly TimeSpan Bucket = TimeSpan.FromMinutes(15);

    /// <summary>A caller without any grant on the facility type.</summary>
    public static ClaimsPrincipal Plain { get; } = CreateUser("anchor-plain", PlainRole);

    /// <summary>A caller that may read the facility.</summary>
    public static ClaimsPrincipal FacilityReader { get; } = CreateUser("anchor-reader", PlainRole, FacilityReaderRole);

    /// <summary>Metering point 1: number <c>AT-0001</c>, status OK. Two raw points, 2 x 10 V.</summary>
    public OctoObjectId MeteringPoint1 { get; } = OctoObjectId.GenerateNewId();

    /// <summary>Metering point 2: number <c>AT-0002</c>, status Maintenance. Two raw points, 2 x 20 V.</summary>
    public OctoObjectId MeteringPoint2 { get; } = OctoObjectId.GenerateNewId();

    /// <summary>The operating facility, protected by the data policy.</summary>
    public OctoObjectId Facility { get; } = OctoObjectId.GenerateNewId();

    public OctoObjectId RawArchive { get; private set; }
    public OctoObjectId TimeRangeArchive { get; private set; }
    public OctoObjectId RollupArchive { get; private set; }
    public OctoObjectId AssetArchive { get; private set; }

    /// <summary>Archive of <c>AccessTestAccount</c> (Hidden attribute <c>PasswordHash</c>); the row points at an account with a canary.</summary>
    public OctoObjectId HiddenArchive { get; private set; }

    /// <summary>Archive of <c>ServiceCredential</c> (Secret attributes, also inside records); the row points at a credential with canaries.</summary>
    public OctoObjectId SecretArchive { get; private set; }

    private OctoObjectId Account { get; } = OctoObjectId.GenerateNewId();
    private OctoObjectId Credential { get; } = OctoObjectId.GenerateNewId();

    public AnchorColumnsStreamDataFixture()
    {
        var table = new RtDataPolicyTable(
        [
            new RtDataPolicyRule("anchor-facility-read", new HashSet<string> { FacilityType }, [RtDataAction.Read],
                OwnedOnly: false, AuditOnly: false, new HashSet<string> { FacilityReaderRole })
        ]);
        Services.AddSingleton<IDataPermissionResolver>(new FixedDataPermissionResolver(table));
    }

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        var systemContext = GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        var repository = systemContext.GetSystemTenantRepository();

        await InsertEntitiesAsync(repository);

        var lifecycle = tenantContext.GetArchiveLifecycleService()
                        ?? throw new InvalidOperationException("ArchiveLifecycleService not registered.");
        var streamRepository = tenantContext.GetStreamDataRepository()
                               ?? throw new InvalidOperationException("StreamDataRepository not available.");
        var meteringPointType = new RtCkId<CkTypeId>(MeteringPointType);
        var assetType = new RtCkId<CkTypeId>(AssetType);

        // Raw archive: two points per metering point, 15 minutes apart.
        RawArchive = await CreateRawArchiveAsync(repository, lifecycle, "AnchorRawArchive", MeteringPointType, "Voltage");
        await streamRepository.InsertAsync(RawArchive,
        [
            Point(MeteringPoint1, meteringPointType, Start, 10.0), Point(MeteringPoint1, meteringPointType, Start.Add(Bucket), 10.0),
            Point(MeteringPoint2, meteringPointType, Start, 20.0), Point(MeteringPoint2, meteringPointType, Start.Add(Bucket), 20.0)
        ]);
        await RefreshArchiveTableAsync(RawArchive.ToString());

        // Time-range archive: one window per metering point and bucket.
        var store = tenantContext.GetTimeRangeArchiveRuntimeStore()
                    ?? throw new InvalidOperationException("TimeRangeArchiveRuntimeStore not available.");
        TimeRangeArchive = await store.InsertAsync("AnchorTimeRangeArchive", meteringPointType,
            new List<CkArchiveColumnSpec> { new("Voltage", Indexed: true, Required: false) }, Bucket);
        await lifecycle.ActivateAsync(TimeRangeArchive);
        await streamRepository.InsertTimeRangeAsync(TimeRangeArchive,
        [
            Window(MeteringPoint1, meteringPointType, Start, 10.0), Window(MeteringPoint1, meteringPointType, Start.Add(Bucket), 10.0),
            Window(MeteringPoint2, meteringPointType, Start, 20.0), Window(MeteringPoint2, meteringPointType, Start.Add(Bucket), 20.0)
        ]);
        await RefreshArchiveTableAsync(TimeRangeArchive.ToString());

        // Rollup of the raw archive: SUM of Voltage per 30-minute bucket and metering point.
        var rollupLifecycle = tenantContext.GetRollupArchiveLifecycleService()
                              ?? throw new InvalidOperationException("Rollup lifecycle service not available.");
        await EnsureJobLogFilterAsync();
        RollupArchive = await rollupLifecycle.CreateAsync("AnchorRollup", [new RollupSourceReference(RawArchive)],
            TimeSpan.FromMinutes(30), TimeSpan.Zero,
            new[] { new CkRollupAggregationSpec("Voltage", CkRollupFunction.Sum, null) });
        await lifecycle.ActivateAsync(RollupArchive);
        var source = await tenantContext.GetArchiveRuntimeStore().GetAsync(RawArchive)
                     ?? throw new InvalidOperationException("Source archive snapshot missing.");
        var rollup = await tenantContext.GetRollupArchiveRuntimeStore()!.GetAsync(RollupArchive)
                     ?? throw new InvalidOperationException("Rollup archive snapshot missing.");
        await ((IArchiveRecomputeExecutor)streamRepository).ExecuteAsync(source, rollup, Start, Start.AddHours(1),
            rtIdScope: null, CancellationToken.None);
        await RefreshArchiveTableAsync(RollupArchive.ToString());
        await RefreshArchiveTableAsync($"{RollupArchive}__genmap");

        // Archive of the base type: a metering point and the protected facility.
        AssetArchive = await CreateRawArchiveAsync(repository, lifecycle, "AnchorAssetArchive", AssetType, "Name");
        await streamRepository.InsertAsync(AssetArchive,
        [
            new StreamDataPoint
            {
                RtId = MeteringPoint1, CkTypeId = assetType, Timestamp = Start,
                Attributes = new Dictionary<string, object?> { ["name"] = "row of the metering point" }
            },
            new StreamDataPoint
            {
                RtId = Facility, CkTypeId = assetType, Timestamp = Start,
                Attributes = new Dictionary<string, object?> { ["name"] = "row of the facility" }
            },
            // An anchor that does not exist (any more): null cells, never an error.
            new StreamDataPoint
            {
                RtId = OctoObjectId.GenerateNewId(), CkTypeId = assetType, Timestamp = Start,
                Attributes = new Dictionary<string, object?> { ["name"] = "row without an anchor" }
            }
        ]);
        await RefreshArchiveTableAsync(AssetArchive.ToString());

        // Archives of the types with protected attributes: the anchors carry canary values.
        HiddenArchive = await CreateRawArchiveAsync(repository, lifecycle, "AnchorHiddenArchive", AccountType, "Name");
        SecretArchive = await CreateRawArchiveAsync(repository, lifecycle, "AnchorSecretArchive", CredentialType, "Name");
        await streamRepository.InsertAsync(HiddenArchive,
        [
            new StreamDataPoint
            {
                RtId = Account, CkTypeId = new RtCkId<CkTypeId>(AccountType), Timestamp = Start,
                Attributes = new Dictionary<string, object?> { ["name"] = "row of the account" }
            }
        ]);
        await streamRepository.InsertAsync(SecretArchive,
        [
            new StreamDataPoint
            {
                RtId = Credential, CkTypeId = new RtCkId<CkTypeId>(CredentialType), Timestamp = Start,
                Attributes = new Dictionary<string, object?> { ["name"] = "row of the credential" }
            }
        ]);
        await RefreshArchiveTableAsync(HiddenArchive.ToString());
        await RefreshArchiveTableAsync(SecretArchive.ToString());
    }

    private async Task EnsureJobLogFilterAsync()
    {
        // The staging copy of a recompute refuses to run unless COPY statements stay out of sys.jobs_log (AB#6944).
        await using var conn = new NpgsqlConnection(CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SET GLOBAL TRANSIENT \"stats.jobs_log_filter\" = 'stmt NOT ILIKE ''copy %'''", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertEntitiesAsync(ITenantRepository repository)
    {
        var entities = new[]
        {
            new RtEntity(new RtCkId<CkTypeId>(MeteringPointType), MeteringPoint1, new Dictionary<string, object?>
            {
                ["Name"] = "Metering point 1", ["Description"] = "d", ["MeteringPointNumber"] = "AT-0001",
                ["MeterReading"] = 1, ["OperatingStatus"] = 1, ["SyncSequenceNumber"] = 0
            }) { RtWellKnownName = "AnchorMeteringPoint1" },
            new RtEntity(new RtCkId<CkTypeId>(MeteringPointType), MeteringPoint2, new Dictionary<string, object?>
            {
                ["Name"] = "Metering point 2", ["Description"] = "d", ["MeteringPointNumber"] = "AT-0002",
                ["MeterReading"] = 2, ["OperatingStatus"] = 2, ["SyncSequenceNumber"] = 0
            }) { RtWellKnownName = "AnchorMeteringPoint2" },
            new RtEntity(new RtCkId<CkTypeId>(FacilityType), Facility, new Dictionary<string, object?>
            {
                ["Name"] = "Secret facility", ["Description"] = "protected by a data policy"
            }) { RtWellKnownName = "AnchorFacility" },
            new RtEntity(new RtCkId<CkTypeId>(AccountType), Account, new Dictionary<string, object?>
            {
                ["Name"] = "Account 1", ["Label"] = "visible label", ["PasswordHash"] = CanaryPasswordHash
            }) { RtWellKnownName = "AnchorAccount" },
            new RtEntity(new RtCkId<CkTypeId>(CredentialType), Credential, new Dictionary<string, object?>
            {
                ["Name"] = "Credential 1", ["Password"] = CanaryPassword, ["ApiKey"] = CanaryApiKey,
                ["PrimaryEndpoint"] = new RtRecord(new RtCkId<CkRecordId>(CredentialEntryRecord),
                    new Dictionary<string, object?>
                        { ["Key"] = "primary", ["Token"] = CanaryToken, ["Label"] = "visible record label" }),
                ["Endpoints"] = new List<RtRecord>
                {
                    new(new RtCkId<CkRecordId>(CredentialEntryRecord),
                        new Dictionary<string, object?> { ["Key"] = "e1", ["Token"] = CanaryEndpointToken })
                }
            }) { RtWellKnownName = "AnchorCredential" }
        };

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        foreach (var entity in entities)
        {
            await repository.InsertOneRtEntityAsync(session, entity);
        }

        await session.CommitTransactionAsync();
    }

    private static async Task<OctoObjectId> CreateRawArchiveAsync(ITenantRepository repository,
        IArchiveLifecycleService lifecycle, string name, string targetType, string column)
    {
        var archive = new RtRawArchive
        {
            RtWellKnownName = name,
            TargetCkTypeId = targetType,
            Status = RtCkArchiveStatusEnum.Created,
            Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
            {
                new() { Path = column, Indexed = true, Required = false }
            }
        };

        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            await repository.InsertOneRtEntityAsync(session, archive);
            await session.CommitTransactionAsync();
        }

        await lifecycle.ActivateAsync(archive.RtId);
        return archive.RtId;
    }

    private static StreamDataPoint Point(OctoObjectId rtId, RtCkId<CkTypeId> type, DateTime timestamp, double voltage) =>
        new()
        {
            RtId = rtId, CkTypeId = type, Timestamp = timestamp,
            Attributes = new Dictionary<string, object?> { ["voltage"] = voltage }
        };

    private static TimeRangeStreamDataPoint Window(OctoObjectId rtId, RtCkId<CkTypeId> type, DateTime from, double voltage) =>
        new()
        {
            RtId = rtId, CkTypeId = type, From = from, To = from.Add(Bucket),
            Attributes = new Dictionary<string, object?> { ["voltage"] = voltage }
        };

    private static ClaimsPrincipal CreateUser(string subject, params string[] roles)
    {
        var claims = new List<Claim> { new("sub", subject), new(ClaimTypes.Name, subject) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "IntegrationTests"));
    }

    private sealed class FixedDataPermissionResolver(RtDataPolicyTable table) : IDataPermissionResolver
    {
        public Task<RtDataPolicyTable> GetPolicyTableAsync(IRuntimeRepository runtimeRepository) =>
            Task.FromResult(table);

        public void Invalidate(string tenantId)
        {
        }
    }
}
