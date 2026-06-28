using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Routing;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Extensions;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Observability;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Extensions;
using NLog;
using NLog.Web;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

// NLog: Set up the logger first to catch all errors
var nLogFactory = LogManager.Setup().RegisterNLogWeb().LoadConfigurationFromFile("nlog.config").LogFactory;
var logger = nLogFactory.GetCurrentClassLogger();

try
{
    logger.Debug("init main");

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = Directory.GetCurrentDirectory(),
        WebRootPath = "wwwroot",
    });

    builder.AddObservability()
        .AddStreamDataHealthCheck()
        .AddSystemContextHealthCheck();

    // NLog: Setup NLog for Dependency injection
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Trace);
    builder.Host.UseNLog();

    // additional providers here needed.
    // allow environment variables to override values from other providers.
    builder.Configuration.AddEnvironmentVariables("OCTO_").AddCommandLine(args)
        .AddUserSecrets(typeof(Program).Assembly, true);

    builder.Services.AddTransient<IDefaultConfigurationCreatorService, DefaultConfigurationCreatorService>();
    builder.Services.AddCors();

    builder.Services.Configure<RouteOptions>(options =>
        options.ConstraintMap.Add("tenantId", typeof(TenantIdRouteConstraint)));

    // Bind blueprint variable context (octo.version/environment/systemTenantId) so the
    // default IBlueprintVariableProvider surfaces values from helm-injected
    // OCTO_BLUEPRINTS__* environment variables instead of falling back to defaults.
    builder.Services.Configure<OctoBlueprintVariablesOptions>(options =>
        builder.Configuration.GetSection(OctoBlueprintVariablesOptions.SectionName).Bind(options));

    builder.Services.AddRuntimeEngine()
        .AddOctoAssetRepositoryServices(
            systemOptions => builder.Configuration.GetSection("System").Bind(systemOptions),
            options => builder.Configuration.GetSection("AssetRepository").Bind(options))
        .AddCrateDbStreamDataRepository<ConfigureStreamDataConfiguration>()
        .AddRollupOrchestratorBackgroundService()
        .AddRecomputeOrchestratorBackgroundService();

    // Bind rollup orchestrator options so the StreamData:Rollup config section can override
    // the defaults (tick interval, startup delay, tenant id list). Composition roots with
    // dynamic tenant discovery can replace IRollupTenantSource after this call.
    builder.Services.Configure<Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData.RollupOrchestratorOptions>(
        builder.Configuration.GetSection("StreamData:Rollup"));

    // Bind recompute orchestrator options (AB#4184) so the StreamData:Recompute config section can
    // override the defaults (tick interval, startup delay). The orchestrator reuses the same
    // IRollupTenantSource registered below, so it ticks the full tenant population too.
    builder.Services.Configure<Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData.RecomputeOrchestratorOptions>(
        builder.Configuration.GetSection("StreamData:Recompute"));

    // Asset-repo is a multi-tenant pod: the default ConfigBasedRollupTenantSource only sees
    // tenants explicitly listed in StreamData:Rollup.TenantIds, which would force operators to
    // hand-maintain that list every time a tenant is provisioned. Replace it with the dynamic
    // SystemContextRollupTenantSource that enumerates every registered tenant on each tick;
    // the per-tenant orchestrator (GetRollupOrchestrator) already skips tenants without
    // StreamData enabled, so this source intentionally returns the full population.
    builder.Services.AddSingleton<
        Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData.IRollupTenantSource,
        Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData.SystemContextRollupTenantSource>();

    // Register the StreamData CK model descriptor so EnableStreamDataAsync auto-imports
    // System.StreamData (including CkRollupArchive) into the tenant. Without this, rollups
    // resolve to "RtCkTypeId 'System.StreamData/CkRollupArchive' not found in CkCache".
    builder.Services.AddSingleton<Meshmakers.Octo.Runtime.Contracts.MongoDb.Services.IStreamDataCkModelDescriptor>(
        _ => new Meshmakers.Octo.Runtime.Contracts.MongoDb.Services.StreamDataCkModelDescriptor(
            Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1.SystemStreamDataCkIds.CkModelId));

    var app = builder.Build();
    app.MapObservability();

    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
    }
    else
        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    {
        app.UseHsts();
    }

    app.UseCors();

    app.UseOctoAssetRepositoryServices();

    app.UseStaticFiles();

    app.Run();
}
catch (Exception ex)
{
    //NLog: catch setup errors
    logger.Error(ex, "Stopped program because of exception");
    throw;
}
finally
{
    // Ensure to flush and stop internal timers/threads before application-exit (Avoid segmentation fault on Linux)
    LogManager.Shutdown();
}

// Make the implicit Program class available to integration tests
namespace Meshmakers.Octo.Backend.AssetRepositoryServices
{
    /// <summary>
    /// Main entry point for the Asset Repository Services application.
    /// This partial class makes the implicitly generated Program class accessible to integration tests.
    /// </summary>
    public partial class Program { }
}