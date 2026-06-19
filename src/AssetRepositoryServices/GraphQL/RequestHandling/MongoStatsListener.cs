using GraphQL.Execution;
using GraphQL.Validation;

using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;

/// <summary>
/// GraphQL document execution listener that reads aggregate MongoDB statistics from the
/// active <see cref="MongoRequestScope"/> at the end of execution and attaches them to the
/// response as <c>extensions.mongoDb</c>. The HTTP middleware in <c>octo-common-services</c>
/// opens the scope per request; this listener consumes it.
/// </summary>
/// <remarks>
/// Why a document listener and not field middleware:
/// <list type="bullet">
///   <item>Fires once per execution, not per field — bounded overhead.</item>
///   <item>Runs after the result is materialised, so the accumulator covers validation,
///         resolution, DataLoader batches and post-processing.</item>
/// </list>
/// If the surface middleware is not registered (e.g. in a service that hasn't called
/// <c>AddObservability</c>), <see cref="MongoRequestScope.Begin"/> wasn't called, no scope
/// is active, and this listener silently no-ops.
/// </remarks>
internal sealed class MongoStatsListener : IDocumentExecutionListener
{
    public Task BeforeExecutionAsync(IExecutionContext context) => Task.CompletedTask;

    public Task AfterValidationAsync(IExecutionContext context, IValidationResult validationResult)
        => Task.CompletedTask;

    public Task AfterExecutionAsync(IExecutionContext context)
    {
        var stats = MongoRequestScope.Current;
        if (stats is null || stats.CommandCount == 0)
        {
            return Task.CompletedTask;
        }

        context.OutputExtensions["mongoDb"] = new Dictionary<string, object?>
        {
            ["totalMs"] = (long)stats.TotalMs,
            ["commandCount"] = stats.CommandCount,
            ["slowestMs"] = (long)stats.SlowestMs,
            ["slowestCommand"] = stats.SlowestCommand
        };

        return Task.CompletedTask;
    }
}
