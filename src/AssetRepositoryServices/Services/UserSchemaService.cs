using AssetRepositoryServices.Resources;
using IdentityModel;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands.Payloads;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Infrastructure.Services;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services;

internal class UserSchemaService : IAsyncInitializationService
{
    private readonly ICommandClient<CreateIdentityDataCommandRequest> _commandClient;
    private readonly OctoAssetRepositoryServicesOptions _octoAssetRepositoryServicesOptions;
    private readonly ISystemContext _systemContext;
  
    

    public UserSchemaService(IOctoService octoService, ICommandClient<CreateIdentityDataCommandRequest> commandClient,
        OctoAssetRepositoryServicesOptions octoAssetRepositoryServicesOptions)
    {
        _systemContext = octoService.SystemContext;
        _commandClient = commandClient;
        _octoAssetRepositoryServicesOptions = octoAssetRepositoryServicesOptions;
    }

    public int Order => 0;

    public async Task InitializeAsync()
    {
        using var session = await _systemContext.GetSystemSessionAsync();
        session.StartTransaction();

        var assetRepConfiguration =
            await _systemContext.GetConfigurationAsync(session, AssetRepositoryServiceConstants.AssetServiceSchemaVersionKey,
                new DefaultConfigurationVersion { Version = -1 });
        if (assetRepConfiguration == null || assetRepConfiguration.Version < AssetRepositoryServiceConstants.AssetServiceSchemaVersionValue)
        {
            CreateIdentityDataCommandRequest createIdentityDataCommandRequest = new(null);
            CreateApiScopes(createIdentityDataCommandRequest);
            CreateApiResources(createIdentityDataCommandRequest);
            CreateClients(createIdentityDataCommandRequest);

            await _commandClient.GetResponse<GenericCommandResponse>(createIdentityDataCommandRequest);
            
            await _systemContext.SetConfigurationAsync(session, AssetRepositoryServiceConstants.AssetServiceSchemaVersionKey,
                new DefaultConfigurationVersion { Version = AssetRepositoryServiceConstants.AssetServiceSchemaVersionValue });
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
            new (CommonConstants.SystemApi, CommonConstants.SystemApiDisplayName)
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
            new (CommonConstants.OctoAdminPanelClientId, 
                AssetTexts.Backend_AssetServices_UserSchema_AdminPanel_DisplayName,
                _octoAssetRepositoryServicesOptions.PublicAdminPanelUrl)
            {
                AllowedGrantTypes = [OidcConstants.GrantTypes.AuthorizationCode],

                RequireConsent = false,
                
                RedirectUris = 
                [
                    _octoAssetRepositoryServicesOptions.PublicAdminPanelUrl.EnsureEndsWith("/")
                ],
                
                PostLogoutRedirectUris = [ _octoAssetRepositoryServicesOptions.PublicAdminPanelUrl.EnsureEndsWith("/") ],
                AllowedCorsOrigins = [ _octoAssetRepositoryServicesOptions.PublicAdminPanelUrl.TrimEnd('/') ],
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
                _octoAssetRepositoryServicesOptions.PublicUrl)
            {
                AllowedGrantTypes = [OidcConstants.GrantTypes.Implicit],
            
                RedirectUris =
                [
                    _octoAssetRepositoryServicesOptions.PublicUrl.EnsureEndsWith("/signin-oidc")
                ],
            
                PostLogoutRedirectUris = [ _octoAssetRepositoryServicesOptions.PublicUrl.EnsureEndsWith("/") ],
                AllowedCorsOrigins = [ _octoAssetRepositoryServicesOptions.PublicUrl.TrimEnd('/') ],
                AllowedScopes =
                [
                    CommonConstants.Scopes.OpenId,
                    CommonConstants.Scopes.Profile,
                    CommonConstants.Scopes.Email,
                    JwtClaimTypes.Role
                ]
            },
            new (CommonConstants.AsserRepositoryServicesSwaggerClientId, 
                AssetTexts.Backend_AssetServices_UserSchema_Swagger_DisplayName, 
                _octoAssetRepositoryServicesOptions.PublicUrl)
            {
                AllowedGrantTypes = [OidcConstants.GrantTypes.AuthorizationCode],
            
                RedirectUris =
                [
                    _octoAssetRepositoryServicesOptions.PublicUrl.EnsureEndsWith("/swagger/oauth2-redirect.html")
                ],
            
                PostLogoutRedirectUris = [ _octoAssetRepositoryServicesOptions.PublicUrl.EnsureEndsWith("/") ],
                AllowedCorsOrigins = [ _octoAssetRepositoryServicesOptions.PublicUrl.TrimEnd('/') ],
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
