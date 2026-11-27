using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     <c>runtime.documentAuditTrail(rtId)</c>: the audit trail of one released document (<c>System/DocumentAuditEntry</c>:
///     release, storno and refused changes), in time order. Only a caller who may read the document gets it: the entries
///     are read with the tenant's own session, and are returned only when the data-permission check of the engine
///     (<see cref="IRtDataPermissionEvaluator" />, the rules of the generic queries) allows the caller to read the
///     document. A document the caller may not see, one that does not exist and one without entries all answer the same
///     empty list, so the answer tells nothing about a hidden document. The field is added only when the tenant's System
///     model has the audit entry type.
/// </summary>
internal static class DocumentAuditTrailQueryField
{
    public const string FieldName = "documentAuditTrail";

    internal static readonly RtCkId<CkTypeId> AuditEntryCkTypeId = new("System/DocumentAuditEntry");

    private const string DocumentRtIdAttribute = "DocumentRtId";
    private const string DocumentCkTypeIdAttribute = "DocumentCkTypeId";
    private const string TimestampAttribute = "Timestamp";

    public static void Add(ObjectGraphType runtimeQuery, IGraphTypesCache graphTypesCache)
    {
        ArgumentNullException.ThrowIfNull(runtimeQuery);
        ArgumentNullException.ThrowIfNull(graphTypesCache);

        var entryType = graphTypesCache.GetTypes().FirstOrDefault(t =>
            string.Equals(t.CkTypeId.ToRtCkId().FullName, AuditEntryCkTypeId.FullName, StringComparison.Ordinal));
        if (entryType == null)
        {
            return;
        }

        runtimeQuery.Field(FieldName, new NonNullGraphType(new ListGraphType(new NonNullGraphType(entryType))))
            .Description("The audit trail of a released document: its release, storno and refused change entries " +
                         "(System/DocumentAuditEntry) in time order. Returned only to callers who may read the " +
                         "document; for a document the caller may not read, or that does not exist, the list is empty.")
            .Argument<NonNullGraphType<OctoObjectIdType>>(Statics.RtIdArg, "RtId of the document.")
            .ResolveAsync(ResolveAsync);
    }

    private static async Task<object?> ResolveAsync(IResolveFieldContext<object?> context)
    {
        try
        {
            var userContext = (GraphQlUserContext)context.UserContext;
            var documentRtId = context.GetArgument<OctoObjectId>(Statics.RtIdArg);
            var repository = userContext.TenantContext.GetTenantRepository();

            // The entries through the index (DocumentRtId, Timestamp), with the tenant's own session: what decides is
            // whether the caller may read the document, checked below before anything is returned.
            var query = RtEntityQueryOptions.Create()
                .FieldFilter(DocumentRtIdAttribute, FieldFilterOperator.Equals, documentRtId.ToString())
                .SortOrder(TimestampAttribute, SortOrders.Ascending);
            List<RtEntity> entries;
            using (var session = await repository.GetSessionAsync().ConfigureAwait(false))
            {
                entries = (await repository.GetRtEntitiesByTypeAsync(session, AuditEntryCkTypeId, query)
                    .ConfigureAwait(false)).Items.ToList();
            }

            if (entries.Count == 0)
            {
                return Array.Empty<RtEntityDto>();
            }

            var caller = Helpers.GetSecurityContext(context.UserContext);
            var evaluator = context.RequestServices!.GetRequiredService<IRtDataPermissionEvaluator>();
            var documentTypes = entries
                .Select(e => e.GetAttributeValueOrDefault(DocumentCkTypeIdAttribute) as string)
                .Distinct(StringComparer.Ordinal).ToList();
            foreach (var documentType in documentTypes)
            {
                if (string.IsNullOrEmpty(documentType))
                {
                    // Fail closed: an entry that does not name the type of its document cannot be judged.
                    return Array.Empty<RtEntityDto>();
                }

                var verdict = await evaluator.EvaluateAsync(repository, caller,
                    new RtEntityId(new RtCkId<CkTypeId>(documentType), documentRtId), RtDataAction.Read,
                    cancellationToken: context.CancellationToken).ConfigureAwait(false);
                if (!verdict.IsAllowed)
                {
                    // NotVisible (also a missing document): exactly the answer for a document without a trail.
                    return Array.Empty<RtEntityDto>();
                }
            }

            return entries.Select(RtEntityDtoType.CreateRtEntityDto).ToList();
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
}
