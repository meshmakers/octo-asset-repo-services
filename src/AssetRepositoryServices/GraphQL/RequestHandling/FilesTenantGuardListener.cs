using GraphQL;
using GraphQL.Execution;
using GraphQL.Validation;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;

/// <summary>
///     Interim tenant guard for System.Files on GraphQL (AB#6624, sibling of the REST guard AB#6579). The route
///     tenant selects the repository, the token tenant is never compared by the generic entity API, and the
///     file roles are matched by name - so a user of tenant A holding <c>FileViewer</c> / <c>FileManagement</c>
///     would reach the files of tenant B. The whole request is refused (FORBIDDEN) when a bearer user token
///     issued for another tenant addresses an operation that touches System.Files; every other operation of
///     that token (cross-tenant topology reads of meshmakers-app, octo-cli, ...) is untouched. Same switch as
///     REST: <c>OCTO_Files__TenantGuard=Enforce|LogOnly</c>. Removed when the platform-wide Enforce (AB#5054) is armed.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal class FilesTenantGuardListener(
    ILogger<FilesTenantGuardListener> logger,
    IOptions<FilesOptions> options) : IDocumentExecutionListener
{
    /// <inheritdoc />
    public Task AfterValidationAsync(IExecutionContext context, IValidationResult validationResult) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task BeforeExecutionAsync(IExecutionContext context)
    {
        if (context.UserContext is not GraphQlUserContext userContext)
        {
            return Task.CompletedTask;
        }

        var tenantId = userContext.TenantId;
        var mismatch = FilesTenantGuard.FindMismatch(userContext.User, tenantId);
        if (mismatch == null || !FilesGraphQlInspector.TouchesFiles(context))
        {
            return Task.CompletedTask;
        }

        var mode = FilesTenantGuard.ParseMode(logger, options.Value.TenantGuard);
        if (!FilesTenantGuard.ShouldRefuse(logger, mismatch, tenantId, mode, "GraphQL"))
        {
            return Task.CompletedTask;
        }

        throw new ExecutionError(FilesTenantGuard.RefusalMessage(tenantId))
        {
            Code = Statics.GraphQlForbidden
        };
    }

    /// <inheritdoc />
    public Task AfterExecutionAsync(IExecutionContext context) => Task.CompletedTask;
}
