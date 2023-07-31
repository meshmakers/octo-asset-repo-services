using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Threading.Tasks;
using AssetRepositoryServices.Resources;
using GraphQL;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using IdentityModel;
using Meshmakers.Octo.Backend.AssetRepositoryServices;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.BuilderExtensions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.Common;
using Meshmakers.Octo.Backend.DistributedCache;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     DI extension methods for adding IdentityServer
/// </summary>
// ReSharper disable once UnusedMember.Global
public static class OctoServiceCollectionExtensions
{
    /// <summary>
    ///     Creates a builder.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <returns></returns>
    private static IOctoBuilder AddOctoBuilder(this IServiceCollection services)
    {
        return new OctoBuilder(services);
    }

    /// <summary>
    ///     Adds Octo.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <returns></returns>
    private static IOctoBuilder AddOcto(this IServiceCollection services)
    {
        JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
        var builder = services.AddOctoBuilder();

        builder.AddRequiredPlatformServices();

        services.AddMemoryCache();
        services.AddDistributedPubSubCache(options =>
        {
            var octoOptions = services.BuildServiceProvider().GetRequiredService<OctoAssetRepositoryServicesOptions>();

            options.Host = octoOptions.RedisCacheHost;
            options.Password = octoOptions.RedisCachePassword;
        });

        services.AddAuthentication(authenticationOptions =>
            {
                authenticationOptions.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                authenticationOptions.DefaultChallengeScheme = BackendCommon.OidcAuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.ExpireTimeSpan = AssetRepositoryServiceConstants.CookieExpireTimeSpan;
                options.Cookie.Name = AssetRepositoryServiceConstants.CookieName;
            })
            .AddOpenIdConnect(BackendCommon.OidcAuthenticationScheme, options =>
            {
                var octoOptions = services.BuildServiceProvider()
                    .GetRequiredService<OctoAssetRepositoryServicesOptions>();

                options.Authority = octoOptions.Authority;
                //options.RequireHttpsMetadata = false;

                options.ClientId = CommonConstants.AssetRepositoryServicesClientId;

                options.Scope.Clear();
                options.Scope.Add(CommonConstants.Scopes.OpenId);
                options.Scope.Add(CommonConstants.Scopes.Profile);
                options.Scope.Add(CommonConstants.Scopes.Email);
                options.Scope.Add(CommonConstants.Scopes.Role);

                options.SaveTokens = true;
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = JwtClaimTypes.Name,
                    RoleClaimType = JwtClaimTypes.Role
                };
            })
            .AddJwtBearer(options =>
            {
                var octoOptions = services.BuildServiceProvider()
                    .GetRequiredService<OctoAssetRepositoryServicesOptions>();
                // base-address of your identity server
                options.Authority = octoOptions.Authority;

                options.TokenValidationParameters.ValidateAudience = false;
            });


        services.AddAuthorization(options =>
        {
            options.AddPolicy(AssetRepositoryServiceConstants.AuthenticatedUserPolicy,
                policyBuilder => policyBuilder.RequireAuthenticatedUser());

            options.AddPolicy(AssetRepositoryServiceConstants.SystemApiReadOnlyPolicy, authorizationPolicyBuilder =>
            {
                // require SystemApiFullAccess or SystemApiReadOnly
                authorizationPolicyBuilder.RequireClaim(BackendCommon.ClaimScope, CommonConstants.SystemApiFullAccess,
                    CommonConstants.SystemApiReadOnly);
            });

            options.AddPolicy(AssetRepositoryServiceConstants.SystemApiReadWritePolicy, authorizationPolicyBuilder =>
            {
                // require SystemApiFullAccess
                authorizationPolicyBuilder.RequireClaim(BackendCommon.ClaimScope, CommonConstants.SystemApiFullAccess);
            });

            options.AddPolicy(AssetRepositoryServiceConstants.TenantApiReadWritePolicy, authorizationPolicyBuilder =>
            {
                authorizationPolicyBuilder.AuthenticationSchemes.Add(CookieAuthenticationDefaults.AuthenticationScheme);
                authorizationPolicyBuilder.AuthenticationSchemes.Add(OidcConstants.AuthenticationSchemes
                    .AuthorizationHeaderBearer);
                authorizationPolicyBuilder.RequireAuthenticatedUser();
            });
        });

        services.AddMvcCore().AddAuthorization();

        services.AddOctoApiVersioningAndDocumentation(options =>
        {
            options.AddXmlDocAssembly<Startup>();
            options.AddXmlDocAssembly<ClientDto>();
            options.Scopes = new Dictionary<string, string>
            {
                {
                    CommonConstants.SystemApiFullAccess,
                    AssetTexts.Backend_AssetServices_Api_FullAccess
                },
                {
                    CommonConstants.SystemApiReadOnly,
                    AssetTexts.Backend_AssetServices_Api_ReadOnlyAccess
                }
            };

            options.ApiTitle = "Octo Asset API";
            options.ApiDescription = "Octo Asset Repository Services.";

            options.ClientId = CommonConstants.AsserRepositoryServicesSwaggerClientId;
            options.AppName = AssetTexts.Backend_AssetServices_UserSchema_Swagger_DisplayName;
        });


        // Hangfire is used to handle background jobs and scheduled jobs
        services.AddHangfire(config =>
        {
            var octoOptions = services.BuildServiceProvider()
                .GetRequiredService<IOptions<OctoAssetRepositoryServicesOptions>>();
            var systemOptions = services.BuildServiceProvider()
                .GetRequiredService<IOptions<OctoSystemConfiguration>>();

            var storageOptions = new MongoStorageOptions
            {
                MigrationOptions = new MongoMigrationOptions
                {
                    MigrationStrategy = new DropMongoMigrationStrategy(),
                    BackupStrategy = new NoneMongoBackupStrategy()
                }
            };
            var mongoUrlBuilder = new MongoUrlBuilder
            {
                DatabaseName = octoOptions.Value.JobDatabaseName,
                Username = systemOptions.Value.AdminUser,
                Password = systemOptions.Value.AdminUserPassword,
                AuthenticationSource = systemOptions.Value.AuthenticationDatabaseName,
                UseTls = systemOptions.Value.UseTls,
                AllowInsecureTls = systemOptions.Value.AllowInsecureTls
            };

            if (systemOptions.Value.DatabaseHost.Contains(","))
            {
                mongoUrlBuilder.Servers =
                    systemOptions.Value.DatabaseHost.Split(",").Select(x => new MongoServerAddress(x));
            }
            else
            {
                mongoUrlBuilder.Server = new MongoServerAddress(systemOptions.Value.DatabaseHost);
            }

            config.UseMongoStorage(mongoUrlBuilder.ToString(), storageOptions);
        });

        // Add GraphQL services and configure options
        services.AddGraphQL(graphQlBuilder => graphQlBuilder
            .AddSchema<OctoSchema>()
            .ConfigureExecutionOptions(options =>
            {
                options.EnableMetrics = true;
                if (options.RequestServices != null)
                {
                    var logger = options.RequestServices.GetRequiredService<ILogger<Startup>>();
                    options.UnhandledExceptionDelegate = ctx =>
                    {
                        logger.LogError(ctx.OriginalException, "{Error} occurred", ctx.OriginalException.Message);
                        return Task.CompletedTask;
                    };
                }
            })
            // Add required services for GraphQL request/response de/serialization
            .AddSystemTextJson() // For .NET Core 3+
            .AddErrorInfoProvider(opt => opt.ExposeExceptionDetails = true)
            .AddDataLoader() // Add required services for DataLoader support
            .AddDocumentListener<OctoSessionListener>()
            .AddUserContextBuilder<TenantUserContextBuilder>()
            .AddGraphTypes(typeof(OctoSchema)
                .Assembly)); // Add all IGraphType implementors in assembly which ChatSchema exists 

        // GraphQL
        services.AddSingleton<IOctoSessionAccessor, OctoSessionAccessor>();
        services.AddSingleton<IDocumentExecuter<OctoSchema>, TenantDocumentExecutor>();

        return builder;
    }

    /// <summary>
    ///     Adds Octo.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="systemOptionsSetupAction">Setup action for Octo system persistence</param>
    /// <param name="setupAction">The setup action of core services options</param>
    /// <returns></returns>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IOctoBuilder AddOcto(this IServiceCollection services,
        Action<OctoSystemConfiguration> systemOptionsSetupAction,
        Action<OctoAssetRepositoryServicesOptions> setupAction)
    {
        services.Configure(systemOptionsSetupAction);
        services.Configure(setupAction);
        return services.AddOcto();
    }
}
