using System.IdentityModel.Tokens.Jwt;
using AssetRepositoryServices.Resources;
using GraphQL;
using GraphQL.Server.Transports.AspNetCore;
using GraphQL.Types.Relay;
using IdentityModel;
using Meshmakers.Octo.Backend.AssetRepositoryServices;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Consumers;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.Configuration.DependencyInjection;
using Meshmakers.Octo.Services.Common;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     DI extension methods for adding IdentityServer
/// </summary>
// ReSharper disable once UnusedMember.Global
public static class RuntimeEngineBuilderExtensions
{
    /// <summary>
    ///     Creates a builder.
    /// </summary>
    /// <param name="builder">The services.</param>
    /// <returns></returns>
    private static IRuntimeEngineBuilder AddOctoAssetRepositoryServices(this IRuntimeEngineBuilder builder)
    {
        JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

        AddServices(builder);

        builder.Services.AddMemoryCache();
        builder.Services.AddAuthentication(authenticationOptions =>
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
                var octoOptions = builder.Services.BuildServiceProvider()
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
                var octoOptions = builder.Services.BuildServiceProvider()
                    .GetRequiredService<OctoAssetRepositoryServicesOptions>();
                // base-address of your identity server
                options.Authority = octoOptions.Authority;

                options.TokenValidationParameters.ValidateAudience = false;
            });


        builder.Services.AddAuthorization(options =>
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

        builder.Services.AddMvcCore().AddAuthorization();

        builder.Services.AddOctoApiVersioningAndDocumentation(options =>
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

        // Add GraphQL services and configure options
        builder.Services.AddGraphQL(graphQlBuilder => graphQlBuilder
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
        builder.Services.AddSingleton<IOctoSessionAccessor, OctoSessionAccessor>();
        builder.Services.AddSingleton<IDocumentExecuter<OctoSchema>, TenantDocumentExecutor>();

        return builder;
    }

    /// <summary>
    ///     Adds Octo.
    /// </summary>
    /// <param name="builder">The services.</param>
    /// <param name="systemOptionsSetupAction">Setup action for Octo system persistence</param>
    /// <param name="setupAction">The setup action of core services options</param>
    /// <returns></returns>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IRuntimeEngineBuilder AddOctoAssetRepositoryServices(this IRuntimeEngineBuilder builder,
        Action<OctoSystemConfiguration> systemOptionsSetupAction,
        Action<OctoAssetRepositoryServicesOptions> setupAction)
    {
        builder.Services.Configure(systemOptionsSetupAction);
        builder.Services.Configure(setupAction);
        return builder.AddOctoAssetRepositoryServices();
    }

    private static void AddServices(IRuntimeEngineBuilder builder)
    {
        builder.Services.AddOptions();
        builder.Services.AddSingleton(
            resolver => resolver.GetRequiredService<IOptions<OctoAssetRepositoryServicesOptions>>().Value);
        builder.Services.AddSingleton(
            resolver => resolver.GetRequiredService<IOptions<OctoSystemConfiguration>>().Value);
        builder.Services.AddSingleton<GraphQLHttpMiddlewareOptions>();
        builder.Services.ConfigureOptions<ConfigureOctoSwaggerOptions>();
        builder.Services.ConfigureOptions<ConfigureDistributionEventHubOptions>();

        // Add GraphQL types (GraphQL.Relay)
        builder.Services.AddTransient(typeof(ConnectionType<>));
        builder.Services.AddTransient(typeof(EdgeType<>));
        builder.Services.AddTransient<PageInfoType>();

        // GraphQL custom services
        builder.Services.AddSingleton<ISchemaContext, SchemaContext>();

        builder.Services.AddOctoServiceInfrastructure("AssetRepositoryService", c =>
        {
            c.AddCommandClient<CreateIdentityDataCommandRequest>("identity::create-identity-data");
            c.AddCommandClient<ExportRtCommandRequest>("bot::export-rt");
            c.AddCommandClient<ImportRtCommandRequest>("bot::import-rt");
            c.AddCommandClient<ImportCkCommandRequest>("bot::import-ck");
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PosUpdateTenant>();
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PreDeleteTenant>();
        });

        // Add the basic services of Octo
        builder.Services.AddRuntimeEngine()
            .AddMongoDbRuntimeRepository();
        builder.Services.AddSingleton<IOctoService, OctoService>();
    }
}