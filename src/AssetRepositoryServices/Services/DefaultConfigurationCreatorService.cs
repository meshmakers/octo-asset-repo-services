using AssetRepositoryServices.Resources;
using Duende.IdentityModel;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands.Payloads;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Files.Generated.System.Files.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.CkModelMigrations;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Migrations;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services;

internal class DefaultConfigurationCreatorService(
    ILogger<DefaultConfigurationCreatorService> logger,
    IDiagnosticsService diagnosticsService,
    IOptions<OctoAssetRepositoryServicesOptions> options,
    ISystemContext systemContext,
    ICommandClient<CreateIdentityDataCommandRequest> createIdentityDataCommandClient,
    OctoAssetRepositoryServicesOptions octoAssetRepositoryServicesOptions,
    ITenantLifecycleStore tenantLifecycleStore,
    ITenantSetupRetryStore tenantSetupRetryStore,
    ICkModelUpgradeService ckModelUpgradeService,
    IRuntimeRepositoryProvider runtimeRepositoryProvider,
    Files.Migration.ReportingFilesSweepRunner reportingFilesSweepRunner,
    Files.FileSystemDefaults fileSystemDefaults)
    : DefaultConfigurationCreatorServiceStandardized(logger, systemContext, createIdentityDataCommandClient,
        AssetRepositoryServiceConstants.AssetServiceIdentityDataVersionKey,
        AssetRepositoryServiceConstants.AssetServiceIdentityDataVersionValue,
        null, // migrationService - the default file root is ensured at tenant start (FileSystemDefaults)
        // AB#6171: CK data migrations of System.Files (GetCkModelIds) run through the standard upgrade path.
        ckModelUpgradeService,
        runtimeRepositoryProvider,
        null, // serviceEnabledKey - the service is auto-enabled
        // Asset-Repo owns the durable tenant-lifecycle record (it runs setup for every tenant and drives
        // identity seeding), so it is the single writer of Creating/Active/Failed states (AB#4348).
        tenantLifecycleStore: tenantLifecycleStore,
        // A setup run that throws is recorded durably and retried in the background (AB#4690).
        tenantSetupRetryStore: tenantSetupRetryStore)
{
    public override async Task InitializeAsync()
    {
        // Reconfigure the log level based on the configuration
        await diagnosticsService.ReconfigureLogLevelAsync(options.Value.MinLogLevel);

        await base.InitializeAsync();
    }

    /// <summary>
    ///     AB#6175: after the System.Files import and the service migrations, moves System.Reporting file data
    ///     of the tenant to System.Files (idempotent sweep; no-op without legacy data). Never fails the start.
    /// </summary>
    protected override async Task StartTenantAsync(string tenantId)
    {
        await base.StartTenantAsync(tenantId);
        // AB#6171 D8: default root "Files"; logs and retries at the next start when System.Files is missing.
        await fileSystemDefaults.EnsureDefaultRootAsync(tenantId);
        await reportingFilesSweepRunner.RunAtTenantStartAsync(tenantId);
    }

    /// <summary>
    ///     Imports System.Files into every tenant (AB#6171 D2 = B'): the platform file system is available
    ///     everywhere, without an enable step and independent of Reporting. Runs inside the setup
    ///     transaction before the service migrations, so the file collection and its indexes exist before
    ///     the default root is seeded. A failure throws and goes through the setup retry path.
    /// </summary>
    protected override async Task ImportCkModelAsync(IOctoAdminSession session, ITenantContext tenantContext)
    {
        OperationResult operationResult = new();
        await tenantContext.ImportCkModelAsync(SystemFilesCkIds.CkModelId, operationResult);
        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            throw InitializationException.ImportCkModelFailed(tenantContext.TenantId,
                operationResult.GetMessages());
        }
    }

    /// <inheritdoc />
    protected override IEnumerable<CkModelIdVersionRange> GetCkModelIds()
    {
        // Any System.Files 1.x: later minors of the model migrate through the standard upgrade path.
        return [new CkModelIdVersionRange($"{SystemFilesCkIds.CkModelId.Name}-[1.0,2.0)")];
    }

    protected override void CreateApiScopes(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        // Scopes are now managed centrally via unified OctoApiFullAccess/OctoApiReadOnly scopes
    }

    protected override void CreateApiResources(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        // API resources are now managed centrally via unified OctoApiFullAccess/OctoApiReadOnly scopes
    }

    protected override void CreateClients(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        createIdentityDataCommandRequest.Clients = new List<DistClientDto>
        {
            new(CommonConstants.AssetRepositoryServicesClientId,
                AssetTexts.Backend_AssetServices_UserSchema_AssetServices_DisplayName,
                octoAssetRepositoryServicesOptions.PublicUrl)
            {
                AllowedGrantTypes = [OidcConstants.GrantTypes.AuthorizationCode],

                RedirectUris =
                [
                    octoAssetRepositoryServicesOptions.PublicUrl.EnsureEndsWith("/signin-oidc")
                ],

                PostLogoutRedirectUris = [octoAssetRepositoryServicesOptions.PublicUrl.EnsureEndsWith("/")],
                AllowedCorsOrigins = [octoAssetRepositoryServicesOptions.PublicUrl.TrimEnd('/')],
                AllowedScopes =
                [
                    CommonConstants.Scopes.OpenId,
                    CommonConstants.Scopes.Profile,
                    CommonConstants.Scopes.Email,
                    JwtClaimTypes.Role,
                    CommonConstants.OctoApiFullAccess,
                ]
            },
            new(CommonConstants.AsserRepositoryServicesSwaggerClientId,
                AssetTexts.Backend_AssetServices_UserSchema_Swagger_DisplayName,
                octoAssetRepositoryServicesOptions.PublicUrl)
            {
                AllowedGrantTypes = [OidcConstants.GrantTypes.AuthorizationCode],

                RedirectUris =
                [
                    octoAssetRepositoryServicesOptions.PublicUrl.EnsureEndsWith("/swagger/oauth2-redirect.html")
                ],

                PostLogoutRedirectUris = [octoAssetRepositoryServicesOptions.PublicUrl.EnsureEndsWith("/")],
                AllowedCorsOrigins = [octoAssetRepositoryServicesOptions.PublicUrl.TrimEnd('/')],
                AllowedScopes =
                [
                    CommonConstants.Scopes.OpenId,
                    CommonConstants.Scopes.Profile,
                    CommonConstants.Scopes.Email,
                    JwtClaimTypes.Role,
                    CommonConstants.OctoApiFullAccess,
                    CommonConstants.OctoApiReadOnly,
                ]
            }
        };
    }
}