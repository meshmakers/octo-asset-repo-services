using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace AssetRepositoryServices.UnitTests.GraphQL.Methods;

/// <summary>
///     A CK cache that knows one method, the way the in-process host reads definitions at startup. The host enforces the
///     declared authorization from that definition (AB#7664), so a test method needs one that admits the test principal;
///     the session-isolation behaviour stays what the test is about.
/// </summary>
internal static class MethodCkCacheFake
{
    public const string Tenant = "meshtest";
    public const string DeclaredRole = "AdminPanelManagement";

    /// <summary>
    ///     Registers a fake <see cref="ICkCacheService" /> holding <paramref name="qualifiedMethodId" /> (<c>Model/Type.Method-1</c>)
    ///     with an optional string parameter <c>note</c>, a string result and the declared role <see cref="DeclaredRole" />.
    /// </summary>
    public static IServiceCollection AddFakeMethodDefinition(this IServiceCollection services, string qualifiedMethodId)
    {
        var split = qualifiedMethodId.LastIndexOf('.');
        var typeId = qualifiedMethodId[..split];
        var methodId = qualifiedMethodId[(split + 1)..];
        var ckId = new CkId<CkTypeId>(typeId + "-1");
        var type = new CkTypeGraph(ckId, new CkCompiledTypeDto
        {
            TypeId = new CkTypeId(typeId[(typeId.IndexOf('/') + 1)..] + "-1"),
            Methods =
            [
                new CkMethodDto
                {
                    MethodId = methodId,
                    Parameters =
                    [
                        new CkMethodParameterDto { Name = "note", ValueType = AttributeValueTypesDto.String, IsOptional = true }
                    ],
                    Result = new CkMethodResultDto { ValueType = AttributeValueTypesDto.String },
                    Authorization = new CkMethodAuthorizationDto { Roles = [DeclaredRole] }
                }
            ]
        });

        var cache = A.Fake<ICkCacheService>();
        A.CallTo(() => cache.IsTenantLoaded(Tenant)).Returns(true);
        CkTypeGraph? found = type;
        A.CallTo(() => cache.TryGetRtCkType(Tenant, A<RtCkId<CkTypeId>>._, out found)).Returns(true)
            .AssignsOutAndRefParameters(type);
        services.AddSingleton(cache);
        services.Configure<Meshmakers.Octo.Services.Methods.CkMethodHandlerOptions>(
            o => o.ValidationTenantIds = [Tenant]);
        return services;
    }

    /// <summary>Runs the startup validation of the registrations (what the service start does), which fills the definitions.</summary>
    public static async Task InitializeMethodsAsync(this IServiceProvider provider)
    {
        foreach (var service in provider.GetServices<IAsyncInitializationService>().OrderBy(s => s.Order))
        {
            await service.InitializeAsync();
        }
    }

    /// <summary>
    ///     Calls the in-process transport as the gateway does: with the principal of an authenticated caller who holds the
    ///     declared role and <c>octo_api</c>.
    /// </summary>
    public static async Task<CkMethodResponse> InvokeAsAuthorizedCallerAsync(this ICkInProcessMethodTransport transport,
        CkMethodRequest request, CancellationToken cancellationToken)
    {
        using var scope = CkMethodPrincipalScope.Begin(new CkMethodPrincipal("sub-1", [DeclaredRole], ["octo_api"]));
        return await transport.InvokeAsync(request, cancellationToken);
    }
}
