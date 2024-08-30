using AssetRepositoryServices.Resources;
using IdentityModel;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands.Payloads;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services;

internal class DefaultConfigurationCreatorService(
    ILogger<DefaultConfigurationCreatorService> logger,
    IDiagnosticsService diagnosticsService,
    IOptions<OctoAssetRepositoryServicesOptions> options,
    ISystemContext systemContext,
    ICommandClient<CreateIdentityDataCommandRequest> commandClient,
    OctoAssetRepositoryServicesOptions octoAssetRepositoryServicesOptions)
    : DefaultConfigurationCreatorServiceBase(logger)
{
    public override async Task InitializeAsync()
    {
        // Reconfigure the log level based on the configuration
        await diagnosticsService.ReconfigureLogLevelAsync(options.Value.MinLogLevel);

        await base.InitializeAsync();
    }

    protected override async Task SetupTenantAsync(string tenantId)
    {
        if (tenantId != systemContext.TenantId)
        {
            // Currently we only support the system tenant.
            return;
        }

        // Do nothing if the system tenant is not existing.
        // Identity Service is creating the system tenant currently.
        if (!await systemContext.IsSystemTenantExistingAsync())
        {
            return;
        }

        // That means that the system tenant database is existing but (currently) not valid.
        // We wait for a PosTenantCreated event to create the default configuration.
        if (!await systemContext.IsCkModelExistingAsync(SystemCkIds.ModelId))
        {
            return;
        }

        logger.LogInformation("Setting up default configuration for tenant '{TenantId}'", tenantId);

        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var assetRepConfiguration =
            await systemContext.GetConfigurationAsync(session,
                AssetRepositoryServiceConstants.AssetServiceSchemaVersionKey,
                new DefaultConfigurationVersion { Version = -1 });
        if (assetRepConfiguration == null || assetRepConfiguration.Version <
            AssetRepositoryServiceConstants.AssetServiceSchemaVersionValue)
        {
            CreateIdentityDataCommandRequest createIdentityDataCommandRequest = new(tenantId);
            CreateApiScopes(createIdentityDataCommandRequest);
            CreateApiResources(createIdentityDataCommandRequest);
            CreateClients(createIdentityDataCommandRequest);

            logger.LogInformation("Creating identity data for tenant '{TenantId}'", tenantId);
            // We retry 5 times to create identity data. This is important because the identity service might not be ready yet.
            var r = await commandClient.GetResponseWithRetry<EnumCommandResponse<CreateIdentityDataResult>>(
                createIdentityDataCommandRequest);
            logger.LogInformation("Create identity data response: {Response}", r.Response);
            if (r.Response == CreateIdentityDataResult.Success)
            {
                await systemContext.SetConfigurationAsync(session,
                    AssetRepositoryServiceConstants.AssetServiceSchemaVersionKey,
                    new DefaultConfigurationVersion
                        { Version = AssetRepositoryServiceConstants.AssetServiceSchemaVersionValue });
            }
            else if (r.Response != CreateIdentityDataResult.FailedTenantHasNoIdentityCk)
            {
                logger.LogInformation("The tenant '{TenantId}' has no identity CK, skipped to create identity data",
                    tenantId);
            }
            else
            {
                logger.LogError("The tenant '{TenantId}' has no identity CK, skipped to create identity data",
                    tenantId);
            }
        }

        await session.CommitTransactionAsync();
    }

    private void CreateApiScopes(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        createIdentityDataCommandRequest.ApiScopes = new List<DistApiScopeDto>
        {
            new(CommonConstants.SystemApiFullAccess,
                CommonConstants.SystemApiFullAccessDisplayName),
            new(CommonConstants.SystemApiReadOnly,
                CommonConstants.SystemApiReadOnlyDisplayName)
        };
    }

    private void CreateApiResources(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        createIdentityDataCommandRequest.ApiResources = new List<DistApiResourcesDto>
        {
            new(CommonConstants.SystemApi, CommonConstants.SystemApiDisplayName)
            {
                Description = CommonConstants.SystemApiDescription,
                IsEnabled = true,
                Scopes = new List<string>
                {
                    CommonConstants.SystemApiFullAccess,
                    CommonConstants.SystemApiReadOnly
                }
            }
        };
    }

    private void CreateClients(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        createIdentityDataCommandRequest.Clients = new List<DistClientDto>
        {
            new(CommonConstants.OctoAdminPanelClientId,
                AssetTexts.Backend_AssetServices_UserSchema_AdminPanel_DisplayName,
                octoAssetRepositoryServicesOptions.PublicAdminPanelUrl)
            {
                AllowedGrantTypes = [OidcConstants.GrantTypes.AuthorizationCode],

                RequireConsent = false,

                RedirectUris =
                [
                    octoAssetRepositoryServicesOptions.PublicAdminPanelUrl.EnsureEndsWith("/")
                ],

                PostLogoutRedirectUris = [octoAssetRepositoryServicesOptions.PublicAdminPanelUrl.EnsureEndsWith("/")],
                AllowedCorsOrigins = [octoAssetRepositoryServicesOptions.PublicAdminPanelUrl.TrimEnd('/')],
                AllowOfflineAccess = true,
                AllowedScopes =
                [
                    CommonConstants.Scopes.OpenId,
                    CommonConstants.Scopes.Profile,
                    CommonConstants.Scopes.Email,
                    JwtClaimTypes.Role,
                    CommonConstants.SystemApiFullAccess,
                    CommonConstants.IdentityApiFullAccess,
                    CommonConstants.BotApiFullAccess
                ]
            },
            new(CommonConstants.AssetRepositoryServicesClientId,
                AssetTexts.Backend_AssetServices_UserSchema_AssetServices_DisplayName,
                octoAssetRepositoryServicesOptions.PublicUrl)
            {
                AllowedGrantTypes = [OidcConstants.GrantTypes.Implicit],

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
                    JwtClaimTypes.Role
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
                    CommonConstants.SystemApiFullAccess,
                    CommonConstants.SystemApiReadOnly
                ]
            }
        };
    }
}