using AssetRepositoryServices.Resources;
using IdentityModel;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands.Payloads;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services;

internal class DefaultConfigurationCreatorService(
    ILogger<DefaultConfigurationCreatorService> logger,
    IDiagnosticsService diagnosticsService,
    IOptions<OctoAssetRepositoryServicesOptions> options,
    ISystemContext systemContext,
    ICommandClient<CreateIdentityDataCommandRequest> createIdentityDataCommandClient,
    OctoAssetRepositoryServicesOptions octoAssetRepositoryServicesOptions)
    : DefaultConfigurationCreatorServiceStandardized(logger, systemContext, createIdentityDataCommandClient,
        AssetRepositoryServiceConstants.AssetServiceIdentityDataVersionKey,
        AssetRepositoryServiceConstants.AssetServiceIdentityDataVersionValue,
        null, // migrationService - we don't need migrations here
        null, // ckModelUpgradeService - we don't need CK model migrations
        null, // runtimeRepositoryProvider - not needed without CK model migrations
        null) // serviceEnabledKey - the service is auto-enabled
{
    public override async Task InitializeAsync()
    {
        // Reconfigure the log level based on the configuration
        await diagnosticsService.ReconfigureLogLevelAsync(options.Value.MinLogLevel);

        await base.InitializeAsync();
    }

    protected override void CreateApiScopes(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        createIdentityDataCommandRequest.ApiScopes = new List<DistApiScopeDto>
        {
            new(CommonConstants.AssetSystemApiFullAccess,
                CommonConstants.AssetSystemApiFullAccessDisplayName),
            new(CommonConstants.AssetSystemApiReadOnly,
                CommonConstants.AssetSystemApiReadOnlyDisplayName),
            new(CommonConstants.AssetTenantApiFullAccess,
                CommonConstants.AssetTenantApiFullAccessDisplayName),
            new(CommonConstants.AssetTenantApiReadOnly,
                CommonConstants.AssetTenantApiReadOnlyDisplayName)
        };
    }

    protected override void CreateApiResources(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        createIdentityDataCommandRequest.ApiResources = new List<DistApiResourcesDto>
        {
            new(CommonConstants.AssetSystemApi, CommonConstants.AssetSystemApiDisplayName)
            {
                Description = CommonConstants.AssetSystemApiDescription,
                IsEnabled = true,
                Scopes = new List<string>
                {
                    CommonConstants.AssetSystemApiFullAccess,
                    CommonConstants.AssetSystemApiReadOnly
                }
            },
            new(CommonConstants.AssetTenantApi, CommonConstants.AssetTenantApiDisplayName)
            {
                Description = CommonConstants.AssetTenantApiDescription,
                IsEnabled = true,
                Scopes = new List<string>
                {
                    CommonConstants.AssetTenantApiFullAccess,
                    CommonConstants.AssetTenantApiReadOnly
                }
            }
        };
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
                    CommonConstants.AssetSystemApiFullAccess,
                    CommonConstants.AssetTenantApiFullAccess,
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
                    CommonConstants.AssetSystemApiFullAccess,
                    CommonConstants.AssetSystemApiReadOnly,
                    CommonConstants.AssetTenantApiFullAccess,
                    CommonConstants.AssetTenantApiReadOnly
                ]
            }
        };
    }
}