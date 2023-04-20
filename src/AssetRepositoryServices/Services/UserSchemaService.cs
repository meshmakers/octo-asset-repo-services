using System.Collections.Generic;
using System.Threading.Tasks;
using AssetRepositoryServices.Resources;
using Duende.IdentityServer.Models;
using IdentityModel;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.SystemEntities;
using Meshmakers.Octo.SystematizedData.Persistence.SystemStores;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services;

internal class UserSchemaService : IUserSchemaService
{
    private readonly IOctoClientStore _clientStore;
    private readonly OctoAssetRepositoryServicesOptions _octoAssetRepositoryServicesOptions;
    private readonly IOctoResourceStore _resourceStore;
    private readonly ISystemContext _systemContext;

    public UserSchemaService(IOctoService octoService, IOctoResourceStore resourceStore, IOctoClientStore clientStore,
        OctoAssetRepositoryServicesOptions octoAssetRepositoryServicesOptions)
    {
        _systemContext = octoService.SystemContext;
        _resourceStore = resourceStore;
        _clientStore = clientStore;
        _octoAssetRepositoryServicesOptions = octoAssetRepositoryServicesOptions;
    }

    public async Task SetupAsync()
    {
        using var session = await _systemContext.StartSystemSessionAsync();
        session.StartTransaction();

        var version =
            await _systemContext.GetConfigurationAsync(session, AssetRepositoryServiceConstants.AssetServiceSchemaVersionKey,
                0);
        if (version < AssetRepositoryServiceConstants.AssetServiceSchemaVersionValue)
        {
            await CreateApiScopes();
            await CreateApiResources();
            await CreateClients();

            await _systemContext.SetConfigurationAsync(session, AssetRepositoryServiceConstants.AssetServiceSchemaVersionKey,
                AssetRepositoryServiceConstants.AssetServiceSchemaVersionValue);
        }

        await session.CommitTransactionAsync();
    }

    private async Task CreateApiScopes()
    {
        await _resourceStore.TryCreateApiScopeAsync(new ApiScope(CommonConstants.SystemApiFullAccess,
            CommonConstants.SystemApiFullAccessDisplayName));
        await _resourceStore.TryCreateApiScopeAsync(new ApiScope(CommonConstants.SystemApiReadOnly,
            CommonConstants.SystemApiReadOnlyDisplayName));
    }

    private async Task CreateApiResources()
    {
        await _resourceStore.GetOrCreateApiResourceAsync(
            new ApiResource(CommonConstants.SystemApi, CommonConstants.SystemApiDisplayName)
            {
                Description = CommonConstants.SystemApiDescription,
                Enabled = true,
                Scopes = new List<string>
                {
                    CommonConstants.SystemApiFullAccess,
                    CommonConstants.SystemApiReadOnly
                }
            });
    }

    private async Task CreateClients()
    {
        var octoAdminPanel = await _clientStore.FindClientByIdAsync(CommonConstants.OctoAdminPanelClientId);
        if (octoAdminPanel == null)
        {
            var octoAdminPanelClient = new OctoClient
            {
                ClientId = CommonConstants.OctoAdminPanelClientId,

                ClientName = AssetTexts.Backend_AssetServices_UserSchema_AdminPanel_DisplayName,
                ClientUri = _octoAssetRepositoryServicesOptions.PublicAdminPanelUrl,

                AllowedGrantTypes = new[] { OidcConstants.GrantTypes.AuthorizationCode },

                RequirePkce = true,
                RequireClientSecret = false,

                AccessTokenType = AccessTokenType.Jwt,
                AllowAccessTokensViaBrowser = true,
                AlwaysIncludeUserClaimsInIdToken = true,
                RequireConsent = false,

                RedirectUris =
                {
                    _octoAssetRepositoryServicesOptions.PublicAdminPanelUrl.EnsureEndsWith("/")
                },

                PostLogoutRedirectUris = { _octoAssetRepositoryServicesOptions.PublicAdminPanelUrl.EnsureEndsWith("/") },
                AllowedCorsOrigins = { _octoAssetRepositoryServicesOptions.PublicAdminPanelUrl.TrimEnd('/') },
                AllowOfflineAccess = true,
                AllowedScopes =
                {
                    CommonConstants.Scopes.OpenId,
                    CommonConstants.Scopes.Profile,
                    CommonConstants.Scopes.Email,
                    JwtClaimTypes.Role,
                    CommonConstants.SystemApiFullAccess,
                    CommonConstants.IdentityApiFullAccess,
                    CommonConstants.BotApiFullAccess
                }
            };
            await _clientStore.CreateAsync(octoAdminPanelClient);
        }
        
        var octoAssetRepositoryServices =
            await _clientStore.FindClientByIdAsync(CommonConstants.AssetRepositoryServicesClientId);
        if (octoAssetRepositoryServices == null)
        {
            var appClient = new OctoClient
            {
                ClientId = CommonConstants.AssetRepositoryServicesClientId,

                ClientName = AssetTexts.Backend_AssetServices_UserSchema_AssetServices_DisplayName,
                ClientUri = _octoAssetRepositoryServicesOptions.PublicUrl,

                AllowedGrantTypes = new[] { OidcConstants.GrantTypes.Implicit },

                RequirePkce = true,
                RequireClientSecret = false,

                AccessTokenType = AccessTokenType.Jwt,
                AllowAccessTokensViaBrowser = true,
                AlwaysIncludeUserClaimsInIdToken = true,

                RedirectUris =
                {
                    _octoAssetRepositoryServicesOptions.PublicUrl.EnsureEndsWith("/signin-oidc")
                },

                PostLogoutRedirectUris = { _octoAssetRepositoryServicesOptions.PublicUrl.EnsureEndsWith("/") },
                AllowedCorsOrigins = { _octoAssetRepositoryServicesOptions.PublicUrl.TrimEnd('/') },
                AllowedScopes =
                {
                    CommonConstants.Scopes.OpenId,
                    CommonConstants.Scopes.Profile,
                    CommonConstants.Scopes.Email,
                    JwtClaimTypes.Role
                }
            };
            await _clientStore.CreateAsync(appClient);
        }

        var octoAssetRepositoryServiceSwaggerClient =
            await _clientStore.FindClientByIdAsync(CommonConstants.AsserRepositoryServicesSwaggerClientId);
        if (octoAssetRepositoryServiceSwaggerClient == null)
        {
            var appClient = new OctoClient
            {
                ClientId = CommonConstants.AsserRepositoryServicesSwaggerClientId,

                ClientName = AssetTexts.Backend_AssetServices_UserSchema_Swagger_DisplayName,
                ClientUri = _octoAssetRepositoryServicesOptions.PublicUrl,

                AllowedGrantTypes = new[] { OidcConstants.GrantTypes.AuthorizationCode },

                RequirePkce = true,
                RequireClientSecret = false,

                AccessTokenType = AccessTokenType.Jwt,
                AllowAccessTokensViaBrowser = true,
                AlwaysIncludeUserClaimsInIdToken = true,

                RedirectUris =
                {
                    _octoAssetRepositoryServicesOptions.PublicUrl.EnsureEndsWith("/swagger/oauth2-redirect.html")
                },

                PostLogoutRedirectUris = { _octoAssetRepositoryServicesOptions.PublicUrl.EnsureEndsWith("/") },
                AllowedCorsOrigins = { _octoAssetRepositoryServicesOptions.PublicUrl.TrimEnd('/') },
                AllowedScopes =
                {
                    CommonConstants.Scopes.OpenId,
                    CommonConstants.Scopes.Profile,
                    CommonConstants.Scopes.Email,
                    JwtClaimTypes.Role,
                    CommonConstants.SystemApiFullAccess,
                    CommonConstants.SystemApiReadOnly
                }
            };
            await _clientStore.CreateAsync(appClient);
        }
    }
}
