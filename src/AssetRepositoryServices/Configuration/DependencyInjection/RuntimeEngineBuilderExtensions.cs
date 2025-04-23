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
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.Configuration.DependencyInjection;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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
                authenticationOptions.DefaultChallengeScheme = InfrastructureCommon.OidcAuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.ExpireTimeSpan = AssetRepositoryServiceConstants.CookieExpireTimeSpan;
                options.Cookie.Name = AssetRepositoryServiceConstants.CookieName;
            })
            .AddOpenIdConnect(InfrastructureCommon.OidcAuthenticationScheme, options =>
            {
                var octoOptions = builder.Services.BuildServiceProvider()
                    .GetRequiredService<OctoAssetRepositoryServicesOptions>();

                options.Authority = octoOptions.Authority;
                //options.RequireHttpsMetadata = false;
                options.ResponseType = "code";

                options.ClientId = CommonConstants.AssetRepositoryServicesClientId;

                options.Scope.Clear();
                options.Scope.Add(CommonConstants.Scopes.OpenId);
                options.Scope.Add(CommonConstants.Scopes.Profile);
                options.Scope.Add(CommonConstants.Scopes.Email);
                options.Scope.Add(CommonConstants.Scopes.Role);
                options.Scope.Add(CommonConstants.AssetTenantApiFullAccess);

                options.SaveTokens = true;

                options.ClaimActions.MapJsonKey("email_verified", "email_verified");
                options.GetClaimsFromUserInfoEndpoint = true;

                options.MapInboundClaims = false; // Don't rename claim types
                options.SaveTokens = true;
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

            options.AddPolicy(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy, authorizationPolicyBuilder =>
            {
                // require SystemApiFullAccess or SystemApiReadOnly
                authorizationPolicyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                    CommonConstants.AssetSystemApiFullAccess,
                    CommonConstants.AssetSystemApiReadOnly);
            });

            options.AddPolicy(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy, authorizationPolicyBuilder =>
            {
                // require SystemApiFullAccess
                authorizationPolicyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                    CommonConstants.AssetSystemApiFullAccess);
            });

            options.AddPolicy(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy, authorizationPolicyBuilder =>
            {
                authorizationPolicyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                    CommonConstants.AssetTenantApiFullAccess);
            });

            options.AddPolicy(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy,
                authorizationPolicyBuilder =>
                {
                    authorizationPolicyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                        CommonConstants.AssetTenantApiFullAccess,
                        CommonConstants.AssetTenantApiReadOnly);

                });
        });

        builder.Services.AddMvcCore().AddAuthorization();

        builder.Services.AddOctoApiVersioningAndDocumentation(options =>
        {
            options.Scopes = new Dictionary<string, string>
            {
                {
                    CommonConstants.AssetSystemApiFullAccess,
                    AssetTexts.Backend_AssetServices_Api_SystemFullAccess
                },
                {
                    CommonConstants.AssetSystemApiReadOnly,
                    AssetTexts.Backend_AssetServices_Api_SystemReadOnlyAccess
                },
                {
                    CommonConstants.AssetTenantApiFullAccess,
                    AssetTexts.Backend_AssetServices_Api_TenantFullAccess
                },
                {
                    CommonConstants.AssetTenantApiReadOnly,
                    AssetTexts.Backend_AssetServices_Api_TenantReadOnlyAccess
                }
            };

            options.PolicyScopeMapping = new Dictionary<string, IEnumerable<string>>
            {
                {
                    AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy,
                    new List<string> { CommonConstants.AssetSystemApiReadOnly }
                },
                {
                    AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy,
                    new List<string> { CommonConstants.AssetSystemApiFullAccess }
                },
                {
                    AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy,
                    new List<string> { CommonConstants.AssetTenantApiReadOnly }
                },
                {
                    AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy,
                    new List<string> { CommonConstants.AssetTenantApiFullAccess }
                }
            };

            options.XmlDocDataTransferObjectAssemblies =
            [
                typeof(ExportModelRequestByQueryDto).Assembly,
                typeof(OctoObjectId).Assembly
            ];
            options.XmlDocOperationAssemblies =
            [
                typeof(Program).Assembly
            ];

            options.ApiTitle = AssetTexts.Api_Title;
            options.ApiDescription = AssetTexts.Api_Description;

            options.ClientId = CommonConstants.AsserRepositoryServicesSwaggerClientId;
            options.AppName = AssetTexts.Backend_AssetServices_UserSchema_Swagger_DisplayName;
        }).AddVersion();

        // Add GraphQL services and configure options
        builder.Services.AddGraphQL(graphQlBuilder => graphQlBuilder
            .ConfigureExecutionOptions(options =>
            {
                options.EnableMetrics = true;
                if (options.RequestServices != null)
                {
                    var logger =
                        options.RequestServices.GetRequiredService<ILogger<OctoAssetRepositoryServicesOptions>>();
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
            .AddUserContextBuilder<TenantUserContextBuilder>()
            .AddGraphTypes() // Add all IGraphType implementors in assembly
            .AddDocumentListener<OctoSessionListener>()
            .Services.Register<IOctoSessionAccessor, OctoSessionAccessor>(GraphQL.DI.ServiceLifetime.Singleton)
        );

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
        builder.Services.ConfigureOptions<ConfigureOctoOpenApiOptions>();
        builder.Services.ConfigureOptions<ConfigureDistributionEventHubOptions>();

        // Add GraphQL types (GraphQL.Relay)
        builder.Services.AddTransient(typeof(ConnectionType<>));
        builder.Services.AddTransient(typeof(EdgeType<>));
        builder.Services.AddTransient<PageInfoType>();

        // GraphQL custom services
        builder.Services.AddSingleton<ISchemaContext, SchemaContext>();


        builder.Services.AddOctoServiceInfrastructure("AssetRepositoryService", c =>
        {
            c.AddCommandClient<CreateIdentityDataCommandRequest>(QueueNames.CreateIdentityDataCommand);
            c.AddCommandClient<ExportRtByQueryCommandRequest>(QueueNames.ExportRtByQueryCommand);
            c.AddCommandClient<ExportRtByDeepGraphCommandRequest>(QueueNames.ExportRtByDeepGraphCommand);
            c.AddCommandClient<ImportRtCommandRequest>(QueueNames.ImportRtCommand);
            c.AddCommandClient<ImportCkCommandRequest>(QueueNames.ImportCkCommand);
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PosUpdateTenant>();
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PreDeleteTenant>();
        });

        // Add the basic services of Octo
        builder.Services.AddRuntimeEngine()
            .AddMongoDbRuntimeRepository();
        builder.Services.AddSingleton<IOctoService, OctoService>();
    }
}