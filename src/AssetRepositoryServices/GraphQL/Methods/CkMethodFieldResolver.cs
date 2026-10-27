using System.Security.Claims;
using Microsoft.Extensions.Logging;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Microsoft.Net.Http.Headers;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;

/// <summary>
///     AB#7026 (F3.7): the resolver of the generated method fields. It hands the call to the authorize-first
///     <see cref="ICkMethodGateway" /> and maps the answer. Order, owned by the gateway: authenticate, authorize (roles,
///     <c>allowSelf</c>, scopes, <c>octo_api</c>), load the target and check the data permission (AB#7037), validate the
///     parameters, dispatch. A refused call is the same <c>METHOD_FORBIDDEN</c> whether the target exists or not, because
///     the resolver reads nothing before the gateway has authorized: the load and the check are a callback the gateway
///     calls after authorization.
/// </summary>
/// <remarks>
///     The target is loaded by a session of its own with the caller's security context
///     (<see cref="RtSecurityContext" />), not part of the request's Mongo transaction (decision 2026-10-07: methods run
///     outside the request transaction); see <see cref="CkMethodTargetAccessChecker" />. Nothing of the parameters, the token or the target id of a refused call reaches a
///     log line or an error: the resolver itself logs nothing, the gateway logs method id and invocation id.
/// </remarks>
internal static class CkMethodFieldResolver
{
    public static async ValueTask<object?> ResolveAsync(IResolveFieldContext<object?> context, CkMethodGraph method)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(method);

        try
        {
            var info = context.FieldDefinition.GetMetadata<CkMethodCallInfo>(Statics.CkMethodCall);
            var userContext = (GraphQlUserContext)context.UserContext;
            var gateway = context.RequestServices!.GetRequiredService<ICkMethodGateway>();

            var isInstance = info.Kind == CkMethodKindDto.Instance;
            var rtId = isInstance ? context.GetArgument<OctoObjectId>(Statics.RtIdArg) : default;
            var targetRtId = isInstance ? rtId.ToString() : null;

            var call = new CkMethodGatewayCall(
                userContext.TenantId,
                info.CkTypeId.FullName,
                info.QualifiedMethodId,
                targetRtId,
                CkMethodParameterJson.ToJson(InputOf(context)),
                userContext.User ?? new ClaimsPrincipal(new ClaimsIdentity()),
                BearerTokenOf(context));

            // Own session without a transaction: the method runs outside the request transaction. Declared authorization
            // runs in the gateway before anything is read; the check below is only called once the caller passed it.
            var securityContext = Helpers.GetSecurityContext(context.UserContext);
            var repository = userContext.TenantContext.GetTenantRepository();
            var evaluator = context.RequestServices!.GetRequiredService<IRtDataPermissionEvaluator>();
            var targetAccess = isInstance
                ? CkMethodTargetAccessChecker.ForInstance(repository, securityContext, evaluator, info.CkTypeId, rtId)
                : CkMethodTargetAccessChecker.ForStatic(repository, securityContext, evaluator, info.CkTypeId);

            var response = await gateway.InvokeAsync(call, targetAccess, context.CancellationToken)
                .ConfigureAwait(false);

            var outcome = CkMethodResponseMapper.Map(response, method, context.GetCkCacheService(), userContext.TenantId,
                context.RequestServices!.GetService<ILoggerFactory>()?.CreateLogger(typeof(CkMethodResponseMapper)));
            if (outcome.Error != null)
            {
                context.Errors.Add(outcome.Error);
                return null;
            }

            return outcome.Value;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            return context.HandleException(e);
        }
    }

    private static IDictionary<string, object?>? InputOf(IResolveFieldContext context) =>
        context.Arguments != null && context.Arguments.TryGetValue(Statics.InputArg, out var argument)
            ? argument.Value as IDictionary<string, object?>
            : null;

    private static string? BearerTokenOf(IResolveFieldContext context)
    {
        var header = context.RequestServices?.GetService<IHttpContextAccessor>()?.HttpContext?.Request.Headers[HeaderNames.Authorization]
            .ToString();
        const string scheme = "Bearer ";
        return header != null && header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            ? header[scheme.Length..].Trim()
            : null;
    }
}
