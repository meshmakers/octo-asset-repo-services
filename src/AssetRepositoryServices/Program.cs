using Meshmakers.Octo.Backend.AssetRepositoryServices;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Routing;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Extensions;
using Meshmakers.Octo.Services.Common.Cors;
using Meshmakers.Octo.Services.Common.StreamData.Extensions;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Observability;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;
using NLog;
using NLog.Web;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

// NLog: setup the logger first to catch all errors
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
    builder.Services.AddSingleton<CorsPolicyProvider>();
    builder.Services.AddSingleton<ICorsPolicyProvider>(p => p.GetRequiredService<CorsPolicyProvider>());
    builder.Services.AddCors();

    builder.Services.Configure<RouteOptions>(options =>
        options.ConstraintMap.Add("tenantId", typeof(TenantIdRouteConstraint)));

    builder.Services.AddRuntimeEngine()
        .AddOctoAssetRepositoryServices(
            systemOptions => builder.Configuration.GetSection("System").Bind(systemOptions),
            options => builder.Configuration.GetSection("AssetRepository").Bind(options));
    
    builder.Services.AddStreamDataManagement()
        .AddStreamDataDatabase(configuration =>
    {
        var assetRepoConfig = builder.Configuration.Get<OctoAssetRepositoryServicesOptions>();
        if (assetRepoConfig == null)
        {
            throw AssetRepositoryException.ServiceNotRegistered(
                typeof(IOptions<OctoAssetRepositoryServicesOptions>));
        }

        configuration.ConnectionString = assetRepoConfig.StreamDataConnectionString;
    });
    
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
          