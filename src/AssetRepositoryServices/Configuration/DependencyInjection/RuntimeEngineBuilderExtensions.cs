using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using AssetRepositoryServices.Resources;
using GraphQL;
using Meshmakers.Common.Shared;
using GraphQL.Server.Transports.AspNetCore;
using GraphQL.Types.Relay;
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
using Meshmakers.Octo.ConstructionKit.Contracts.ModelCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.Configuration.DependencyInjection;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

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

                options.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = ctx =>
                    {
                        // If the request is an API request or an AJAX request, return 401 Unauthorized
                        if (!ctx.Request.Path.Value?.ToLower().Contains("graphql/playground",
                                StringComparison.CurrentCultureIgnoreCase) ?? true)
                        {
                            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return Task.CompletedTask;
                        }

                        ctx.Response.Redirect(ctx.RedirectUri);
                        return Task.CompletedTask;
                    },
                    OnRedirectToAccessDenied = ctx =>
                    {
                        // If the request is an API request or an AJAX request, return 403 Forbidden
                        if (!ctx.Request.Path.Value?.ToLower().Contains("graphql/playground",
                                StringComparison.CurrentCultureIgnoreCase) ?? true)
                        {
                            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return Task.CompletedTask;
                        }

                        ctx.Response.Redirect(ctx.RedirectUri);
                        return Task.CompletedTask;
                    }
                };
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
                options.Scope.Add(CommonConstants.OctoApiFullAccess);

                options.SaveTokens = true;

                options.ClaimActions.MapJsonKey("email_verified", "email_verified");
                options.GetClaimsFromUserInfoEndpoint = true;

                options.MapInboundClaims = false; // Don't rename claim types
                options.SaveTokens = true;

                options.Events = new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectEvents
                {
                    OnRedirectToIdentityProvider = context =>
                    {
                        var tenantId = AssetRepositoryServiceConstants.GetTenantId(context.HttpContext);
                        if (!string.IsNullOrEmpty(tenantId))
                        {
                            context.ProtocolMessage.AcrValues = $"tenant:{tenantId}";
                        }

                        return Task.CompletedTask;
                    }
                };
            })
            .AddJwtBearer(options =>
            {
                var octoOptions = builder.Services.BuildServiceProvider()
                    .GetRequiredService<OctoAssetRepositoryServicesOptions>();
                // base-address of your identity server.
                // EnsureEndsWith("/") mirrors what identity / bot / communication-controller
                // do — tokens from IdentityServer carry `iss` with a trailing slash, so
                // ValidIssuer must match the slash-form exactly.
                var authorityUrl = octoOptions.Authority.EnsureEndsWith("/");
                options.Authority = authorityUrl;

                options.TokenValidationParameters.ValidateAudience = false;

                // Explicitly set the valid issuer so token validation does not depend on fetching
                // the OIDC discovery document. This prevents IDX10204 errors when the identity
                // service is temporarily unreachable (e.g. during rolling updates).
                options.TokenValidationParameters.ValidIssuer = authorityUrl;
            });


        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(AssetRepositoryServiceConstants.AuthenticatedUserPolicyGraphApi, policy =>
            {
                policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme,
                    CookieAuthenticationDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
            });

            options.AddPolicy(AssetRepositoryServiceConstants.AuthenticatedUserPolicy,
                policy => { policy.RequireAuthenticatedUser(); });

            options.AddPolicy(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy,
                authorizationPolicyBuilder =>
                {
                    authorizationPolicyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                        CommonConstants.OctoApiFullAccess,
                        CommonConstants.OctoApiReadOnly);
                });

            options.AddPolicy(AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy,
                authorizationPolicyBuilder =>
                {
                    authorizationPolicyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                        CommonConstants.OctoApiFullAccess);
                });

            options.AddPolicy(AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy,
                authorizationPolicyBuilder =>
                {
                    authorizationPolicyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                        CommonConstants.OctoApiFullAccess);
                });

            options.AddPolicy(AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy,
                authorizationPolicyBuilder =>
                {
                    authorizationPolicyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                        CommonConstants.OctoApiFullAccess,
                        CommonConstants.OctoApiReadOnly);
                });

            options.AddPolicy(AssetRepositoryServiceConstants.DataModelManagementPolicy,
                authorizationPolicyBuilder =>
                {
                    authorizationPolicyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                        CommonConstants.OctoApiFullAccess,
                        CommonConstants.OctoApiDataModelManagement);
                });
        });

        builder.Services.AddMvcCore().AddAuthorization();

        builder.Services.AddOctoApiVersioningAndDocumentation(options =>
        {
            options.Scopes = new Dictionary<string, string>
            {
                {
                    CommonConstants.OctoApiFullAccess,
                    CommonConstants.OctoApiFullAccessDisplayName
                },
                {
                    CommonConstants.OctoApiReadOnly,
                    CommonConstants.OctoApiReadOnlyDisplayName
                },
                {
                    CommonConstants.OctoApiDataModelManagement,
                    CommonConstants.OctoApiDataModelManagementDisplayName
                }
            };

            options.PolicyScopeMapping = new Dictionary<string, IEnumerable<string>>
            {
                {
                    AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy,
                    new List<string> { CommonConstants.OctoApiReadOnly }
                },
                {
                    AssetRepositoryServiceConstants.SystemAssetApiReadWritePolicy,
                    new List<string> { CommonConstants.OctoApiFullAccess }
                },
                {
                    AssetRepositoryServiceConstants.TenantAssetApiReadOnlyPolicy,
                    new List<string> { CommonConstants.OctoApiReadOnly }
                },
                {
                    AssetRepositoryServiceConstants.TenantAssetApiReadWritePolicy,
                    new List<string> { CommonConstants.OctoApiFullAccess }
                },
                {
                    AssetRepositoryServiceConstants.DataModelManagementPolicy,
                    new List<string> { CommonConstants.OctoApiDataModelManagement }
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
            .AddSystemTextJson(c=>
            {
                c.Converters.Add(new CkIdAttributeIdConverter());
                c.Converters.Add(new CkIdAssociationRoleIdConverter());
                c.Converters.Add(new CkIdTypeIdConverter());
                c.Converters.Add(new CkIdRecordIdConverter());
                c.Converters.Add(new CkIdEnumIdConverter());
                c.Converters.Add(new CkModelIdConverter());

                c.Converters.Add(new RtCkIdAttributeIdConverter());
                c.Converters.Add(new RtCkIdAssociationRoleIdConverter());
                c.Converters.Add(new RtCkIdTypeIdConverter());
                c.Converters.Add(new RtCkIdRecordIdConverter());
                c.Converters.Add(new RtCkIdEnumIdConverter());

                c.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            }) // For .NET Core 3+
            .AddErrorInfoProvider(opt => opt.ExposeExceptionDetails = true)
            .AddDataLoader() // Add required services for DataLoader support
            .AddUserContextBuilder<TenantUserContextBuilder>()
            .AddGraphTypes() // Add all IGraphType implementors in assembly
            .AddDocumentListener<OctoSessionListener>()
            .AddDocumentListener<MongoStatsListener>()
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
        builder.Services.AddSingleton(resolver =>
            resolver.GetRequiredService<IOptions<OctoAssetRepositoryServicesOptions>>().Value);
        builder.Services.AddSingleton(resolver =>
            resolver.GetRequiredService<IOptions<OctoSystemConfiguration>>().Value);
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
            c.AddCommandClient<ImportCkBatchCommandRequest>(QueueNames.ImportCkBatchCommand);
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PosUpdateTenant>();
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PreDeleteTenant>();
        });

        // Mongo-backed repositories + blueprint history/installations.
        // Don't call AddRuntimeEngine() here: Program.cs already does, and
        // a second call duplicates every IEnumerable-style registration
        // (most visibly the three IBlueprintCatalog instances surfaced via
        // BlueprintsQuery.catalogs in the studio).
        builder
            .AddMongoDbRuntimeRepository()
            .AddMongoBlueprintSupport();

        // Override engine's default LoggingBlueprintNotifications with the event-hub adapter
        builder.Services.AddSingleton<IBlueprintNotifications, DistributedBlueprintNotifications>();

        // Bind CK model catalog options to configuration sections (if available)
        // This allows OCTO_LocalFileSystemCatalog__IsEnabled etc. env vars to work
        var tempProvider = builder.Services.BuildServiceProvider();
        var config = tempProvider.GetService<IConfiguration>();
        if (config != null)
        {
            builder.Services.Configure<LocalFileSystemCatalogOptions>(
                config.GetSection("LocalFileSystemCatalog"));
            builder.Services.Configure<PrivateGitHubCatalogOptions>(
                config.GetSection("PrivateOctoGitHub"));
            builder.Services.Configure<PublicGitHubCatalogOptions>(
                config.GetSection("PublicOctoGitHub"));
        }
        builder.Services.AddSingleton<IOctoService, OctoService>();
    }
}