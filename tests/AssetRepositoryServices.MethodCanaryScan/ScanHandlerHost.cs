using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub.Configuration.Options;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Methods;
using MethodCanaryScanCkModel.Generated.MethodCanaryScan.v1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.MethodCanaryScan;

/// <summary>
///     The handler of <c>MethodCanaryScan/CanaryAccount.Rotate-1</c>: answers by <c>mode</c>. The failing modes put the secret and
///     the caller's token into the exception text on purpose: the host must mask them before anything is logged.
/// </summary>
public sealed class RotateHandler : ICkMethodHandler<CanaryAccountRotateParameters>
{
    public async Task<CkMethodOutcome> HandleAsync(CkMethodContext<CanaryAccountRotateParameters> context,
        CancellationToken cancellationToken)
    {
        var secret = context.Parameters.Secret;
        switch (context.Parameters.Mode)
        {
            case "fail":
                throw new InvalidOperationException($"secret store rejected {secret} for token {context.CallerAccessToken}");
            case "failjson":
                // a serializer exception that quotes the payload in its JSON-escaped form (AB#7015)
                throw new JsonException("payload " + JsonSerializer.Serialize(new { secret, token = context.CallerAccessToken }));
            case "hang":
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                return CkMethodOutcome.Ok();
            case "invalid":
                return CkMethodOutcome.ValidationFailed("secret", "too-weak");
            case "declared":
                return CkMethodOutcome.Fail("CANARY_REJECTED", ("reason", "weak"));
            default:
                return CkMethodOutcome.Ok();
        }
    }
}

/// <summary>The handler of <c>Guarded-1</c>; never called, the declared role is held by nobody.</summary>
public sealed class GuardedHandler : ICkMethodHandler<CanaryAccountGuardedParameters>
{
    public Task<CkMethodOutcome> HandleAsync(CkMethodContext<CanaryAccountGuardedParameters> context,
        CancellationToken cancellationToken) => Task.FromResult(CkMethodOutcome.Ok());
}

/// <summary>
///     A remote method handler host for the scan model, connected to the broker of the local stack, built like the identity
///     service hosts its methods: <c>AddCkMethodHandlers</c> with discovery, caller token validation against the local identity
///     service, the method definitions of the compiled scan model (the same file that was imported into the tenant). Its logs and
///     activities go to the <see cref="CaptureSink" />.
/// </summary>
internal sealed class ScanHandlerHost : IAsyncDisposable
{
    public const string ServiceName = "MethodCanaryScan";

    private readonly ServiceProvider _provider;
    private readonly IEventHubControl _hub;

    private ScanHandlerHost(ServiceProvider provider, IEventHubControl hub)
    {
        _provider = provider;
        _hub = hub;
    }

    public static async Task<ScanHandlerHost> StartAsync(ScanSettings settings, CaptureSink sink, CancellationToken ct)
    {
        var compiledType = await LoadCompiledTypeAsync();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(sink));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IHostEnvironment>(new ScanHostEnvironment());
        services.AddSingleton(FakeCkCache(settings.TenantId, compiledType));
        services.Configure<CkMethodHandlerOptions>(o => o.ValidationTenantIds = [settings.TenantId]);
        services.Configure<DistributionEventHubOptions>(o =>
        {
            o.InstancePrefix = settings.InstancePrefix;
            o.BrokerHost = settings.BrokerHost;
            o.BrokerPort = settings.BrokerPort;
            o.BrokerUser = settings.BrokerUser;
            o.BrokerPassword = settings.BrokerPassword;
        });
        services.AddDistributionEventHub(c =>
        {
            c.UniqueServiceAddress = ServiceName + "-" + Guid.NewGuid().ToString("N")[..8];
            c.AutomaticallyStartBusDuringStartup = false;
            c.AddCkMethodHandlerDiscovery();
        });
        services.AddCkMethodHandlers(ServiceName, m =>
        {
            m.Handle<RotateHandler>(MethodCanaryScanCkIds.CanaryAccountRotateMethodId);
            m.Handle<GuardedHandler>(MethodCanaryScanCkIds.CanaryAccountGuardedMethodId);
        });
        services.AddCkMethodCallerTokenValidation(o =>
        {
            o.Authority = settings.IdentityUri.ToString();
            o.ValidIssuer = settings.IdentityUri.ToString();
        });
        services.AddHttpClient(Meshmakers.Octo.Services.Methods.Authorization.CkMethodTokenOptions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            });

        var provider = services.BuildServiceProvider();
        var hub = provider.GetRequiredService<IEventHubControl>();
        await hub.StartAsync();
        using (var scope = provider.CreateScope())
        {
            foreach (var service in scope.ServiceProvider.GetServices<IAsyncInitializationService>().OrderBy(s => s.Order))
            {
                await service.InitializeAsync();
            }
        }

        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(ct);
        }

        return new ScanHandlerHost(provider, hub);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hosted in _provider.GetServices<IHostedService>())
        {
            try
            {
                await hosted.StopAsync(CancellationToken.None);
            }
            catch
            {
                // best effort: the queue is auto-delete
            }
        }

        try
        {
            await _hub.StopAsync();
        }
        catch
        {
            // best effort
        }

        await _provider.DisposeAsync();
    }

    /// <summary>The compiled <c>CanaryAccount</c> type from the model embedded in the generated assembly.</summary>
    private static async Task<CkCompiledTypeDto> LoadCompiledTypeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConstructionKit();
        await using var provider = services.BuildServiceProvider();
        var model = new MethodCanaryScanCkEmbeddedModel(provider.GetRequiredService<ICkYamlSerializer>());
        var result = new OperationResult();
        var root = await model.GetCompiledModelRootAsync(result);
        return root.Types!.Single(t => t.TypeId.ToString().StartsWith("CanaryAccount", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A CK cache that knows the tenant and the scan type, the way the host reads definitions (startup validation, declared
    ///     authorization, sensitive parameters). The handler host needs no database.
    /// </summary>
    private static ICkCacheService FakeCkCache(string tenantId, CkCompiledTypeDto compiled)
    {
        var graph = new CkTypeGraph(new CkId<CkTypeId>("MethodCanaryScan-1.0.0/" + compiled.TypeId), compiled);
        var cache = A.Fake<ICkCacheService>();
        A.CallTo(() => cache.IsTenantLoaded(tenantId)).Returns(true);
        CkTypeGraph? found = graph;
        A.CallTo(() => cache.TryGetRtCkType(tenantId, A<RtCkId<CkTypeId>>._, out found)).Returns(true)
            .AssignsOutAndRefParameters(graph);
        return cache;
    }

    private sealed class ScanHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = ServiceName;
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
