using System.Security.Claims;
using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Services.Methods.Operations;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;

/// <summary>
///     AB#8444 (F3.6): <c>runtime.operations</c>, the read surface of the operations of asynchronous CK methods. With
///     <c>operationId</c> it returns that one operation (or <c>OPERATION_NOT_FOUND</c>); without, a page of the caller's
///     own operations, or with <c>allOwners: true</c> every caller's operations of the tenant (tenant administrators).
///     The filters <c>methodId</c>, <c>state</c> and <c>since</c> apply in both forms. Reads go through
///     <see cref="ICkMethodOperationReader" /> (access rules) and the store contract of AB#8443; nothing here writes.
/// </summary>
internal static class CkMethodOperationQueryField
{
    public const string FieldName = "operations";
    public const string OperationIdArg = "operationId";
    public const string MethodIdArg = "methodId";
    public const string StateArg = "state";
    public const string SinceArg = "since";
    public const string AllOwnersArg = "allOwners";

    public static void Add(ObjectGraphType runtimeQuery)
    {
        ArgumentNullException.ThrowIfNull(runtimeQuery);

        runtimeQuery.Field<CkMethodOperationConnectionGraphType>(FieldName)
            .Description("Operations of asynchronous CK methods (the handles returned by their mutations): state, " +
                         "progress and the result or error. With operationId: that operation, readable by the caller " +
                         "who started it and by tenant administrators; an unknown, expired or foreign id is " +
                         "OPERATION_NOT_FOUND. Without: the caller's own operations, newest first; allOwners: true " +
                         "lists every caller's operations of the tenant (tenant administrators only, otherwise " +
                         "OPERATION_QUERY_REFUSED). Operations are kept 7 days after completion by default.")
            .Argument<IdGraphType>(OperationIdArg, "Returns the operation with this id (the handle's operationId).")
            .Argument<StringGraphType>(MethodIdArg,
                "Only operations of this method (qualified, version-less method id, e.g. 'System.Bot/Tenant.Dump-1').")
            .Argument<CkMethodOperationStateGraphType>(StateArg, "Only operations in this state.")
            .Argument<UtcDateTimeGraphType>(SinceArg, "Only operations accepted at or after this time (UTC).")
            .Argument<BooleanGraphType>(AllOwnersArg,
                "List the operations of every caller of the tenant (tenant administrators only). Default false.",
                argument => argument.DefaultValue = false)
            .Argument<IntGraphType>(Statics.SkipArg, "Number of operations to skip (default 0).",
                argument => argument.DefaultValue = 0)
            .Argument<IntGraphType>(Statics.TakeArg,
                $"Page size, 1 to {CkMethodOperationQuery.MaxTake} (default {CkMethodOperationQuery.DefaultTake}).",
                argument => argument.DefaultValue = CkMethodOperationQuery.DefaultTake)
            .ResolveAsync(ResolveAsync);
    }

    private static async Task<object?> ResolveAsync(IResolveFieldContext<object?> context)
    {
        try
        {
            var userContext = (GraphQlUserContext)context.UserContext;
            var user = userContext.User ?? new ClaimsPrincipal(new ClaimsIdentity());
            var reader = context.RequestServices!.GetRequiredService<ICkMethodOperationReader>();

            var methodId = context.GetArgument<string?>(MethodIdArg);
            var state = context.GetArgument<CkMethodOperationState?>(StateArg);
            var since = context.GetArgument<DateTime?>(SinceArg);

            var operationId = context.GetArgument<string?>(OperationIdArg);
            if (operationId != null)
            {
                var record = await reader.GetAsync(userContext.TenantId, user, operationId, context.CancellationToken)
                    .ConfigureAwait(false);
                // the filters of the list form also narrow a read by id; a record outside them is not found, alike
                if ((methodId != null && !string.Equals(record.MethodId, methodId.Trim(), StringComparison.Ordinal)) ||
                    (state != null && record.State != state) ||
                    (since != null && record.AcceptedAtUtc < since.Value))
                {
                    throw CkMethodOperationQueryException.NotFound();
                }

                return new CkMethodOperationConnection([record], 1);
            }

            var filter = new CkMethodOperationListFilter(
                methodId,
                state,
                since,
                context.GetArgument<bool?>(AllOwnersArg) ?? false,
                context.GetArgument<int?>(Statics.SkipArg) ?? 0,
                context.GetArgument<int?>(Statics.TakeArg) ?? CkMethodOperationQuery.DefaultTake);
            var page = await reader.ListAsync(userContext.TenantId, user, filter, context.CancellationToken)
                .ConfigureAwait(false);
            return new CkMethodOperationConnection(page.Items, page.TotalCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CkMethodOperationQueryException e)
        {
            context.Errors.Add(CreateError(e));
            return null;
        }
        catch (Exception e)
        {
            return context.HandleException(e);
        }
    }

    /// <summary>ONE error with <c>extensions.code</c> and <c>extensions.parameters</c>, like a method error.</summary>
    internal static ExecutionError CreateError(CkMethodOperationQueryException exception) =>
        new(exception.Message)
        {
            Code = exception.Code,
            Extensions = new Dictionary<string, object?>
            {
                [CkMethodGraphQlError.CodeKey] = exception.Code,
                [CkMethodGraphQlError.ParametersKey] = exception.Parameters
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal)
            }
        };
}
