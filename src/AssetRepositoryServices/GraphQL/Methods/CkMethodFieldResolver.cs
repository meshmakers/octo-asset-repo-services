using System.Security.Claims;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Microsoft.Net.Http.Headers;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;

/// <summary>
///     AB#7026 (F3.7): the resolver of the generated method fields. It hands the call to the authorize-first
///     <see cref="ICkMethodGateway" /> and maps the answer. Order, owned by the gateway: authenticate, authorize (roles,
///     <c>allowSelf</c>, scopes, <c>octo_api</c>), load the target, validate the parameters, dispatch. A refused call is
///     the same <c>METHOD_FORBIDDEN</c> whether the target exists or not, because the resolver reads nothing before the
///     gateway has authorized: the existence read is a callback the gateway calls after authorization.
/// </summary>
/// <remarks>
///     The existence read is a plain read of its own session with the caller's security context
///     (<see cref="RtSecurityContext" />), not part of the request's Mongo transaction (decision 2026-10-07: methods run
///     outside the request transaction). Nothing of the parameters, the token or the target id of a refused call reaches a
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

            Func<CancellationToken, Task<bool>>? targetExists = null;
            if (isInstance)
            {
                var securityContext = Helpers.GetSecurityContext(context.UserContext);
                var repository = userContext.TenantContext.GetTenantRepository();
                targetExists = async _ =>
                {
                    // Own session without a transaction: the method runs outside the request transaction.
                    using var session = repository.GetSession(securityContext);
                    var found = await repository.GetRtEntitiesByIdAsync(session, info.CkTypeId, [rtId],
                        RtEntityQueryOptions.Create()).ConfigureAwait(false);
                    return found.Items.Any();
                };
            }

            var response = await gateway.InvokeAsync(call, targetExists, context.CancellationToken)
                .ConfigureAwait(false);

            var outcome = CkMethodResponseMapper.Map(response, method, context.GetCkCacheService(), userContext.TenantId);
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
