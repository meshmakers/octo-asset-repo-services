using GraphQL;
using GraphQL.Execution;
using GraphQL.Validation;

using GraphQLParser.AST;

using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;

/// <summary>
///     Options for the GraphQL security hardening (AB#4973).
/// </summary>
public class GraphQlSecurityOptions
{
    /// <summary>
    ///     When true, mutations from tokens without the full <c>octo_api</c> scope are rejected.
    ///     Default false: violations are only logged (log-first release, migration inventory) —
    ///     flip via configuration <c>GraphQl:EnforceMutationScope</c> after the fleet is clean.
    /// </summary>
    public bool EnforceMutationScope { get; set; }
}

/// <summary>
///     Closes the GraphQL scope gap (AB#4973): the REST surface enforces <c>octo_api</c> for writes,
///     but GraphQL mutations only required an authenticated user — a read-only-scoped token could
///     mutate. Ships log-first (see <see cref="GraphQlSecurityOptions.EnforceMutationScope" />).
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal class MutationScopeListener(
    ILogger<MutationScopeListener> logger,
    IOptions<GraphQlSecurityOptions> options) : IDocumentExecutionListener
{
    /// <inheritdoc />
    public Task AfterValidationAsync(IExecutionContext context, IValidationResult validationResult)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task BeforeExecutionAsync(IExecutionContext context)
    {
        if (context.Operation?.Operation != OperationType.Mutation)
        {
            return Task.CompletedTask;
        }

        var user = (context.UserContext as GraphQlUserContext)?.User;
        var scopes = user?.FindAll("scope")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal) ?? [];

        if (scopes.Contains(CommonConstants.OctoApiFullAccess))
        {
            return Task.CompletedTask;
        }

        var subject = user?.FindFirst("sub")?.Value ?? user?.FindFirst("client_id")?.Value ?? "<unknown>";
        if (options.Value.EnforceMutationScope)
        {
            throw new ExecutionError(
                $"GraphQL mutations require the '{CommonConstants.OctoApiFullAccess}' scope.")
            {
                Code = Statics.GraphQlForbidden
            };
        }

        logger.LogWarning(
            "GraphQL mutation by subject '{Subject}' without the '{Scope}' scope — would be rejected once " +
            "GraphQl:EnforceMutationScope is enabled (AB#4973 log-first)",
            subject, CommonConstants.OctoApiFullAccess);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AfterExecutionAsync(IExecutionContext context)
    {
        return Task.CompletedTask;
    }
}
