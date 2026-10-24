using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;

/// <summary>
///     AB#7037: the data-permission check of a method call. It loads the target through a session bound to the caller's
///     <see cref="RtSecurityContext" /> (so a record a data permission hides is not found), then asks the public check of
///     the engine (<see cref="IRtDataPermissionEvaluator" />, the rules the generic mutations enforce) for the data action of
///     the method on the <b>stored</b> type of the record, which may be a type derived from the one in the field. The
///     session is its own and not part of the request's Mongo transaction (decision 2026-10-07: methods run outside the
///     request transaction). Nothing is logged here; the gateway logs method id and invocation id only.
/// </summary>
internal static class CkMethodTargetAccessChecker
{
    /// <summary>The check of an instance method: load the target, then judge the data action on it.</summary>
    public static CkMethodTargetAccessCheck ForInstance(ITenantRepository repository, RtSecurityContext caller,
        IRtDataPermissionEvaluator evaluator, RtCkId<CkTypeId> ckTypeId, OctoObjectId rtId) =>
        async (dataAccess, cancellationToken) =>
        {
            RtEntity? target;
            using (var session = repository.GetSession(caller))
            {
                var found = await repository.GetRtEntitiesByIdAsync(session, ckTypeId, [rtId], RtEntityQueryOptions.Create())
                    .ConfigureAwait(false);
                target = found.Items.FirstOrDefault();
            }

            if (target == null)
            {
                return CkMethodTargetAccess.NotVisible;
            }

            // Fail closed: a stored entity without a type cannot be judged, the call fails instead of passing.
            var storedTypeId = target.CkTypeId ??
                               throw new InvalidOperationException("The stored target carries no CK type id.");
            var verdict = await evaluator.EvaluateAsync(repository, caller, new RtEntityId(storedTypeId, target.RtId),
                ActionOf(dataAccess), publishAudit: true, cancellationToken).ConfigureAwait(false);
            return Map(verdict);
        };

    /// <summary>The check of a static method: only the declaring type is judged.</summary>
    public static CkMethodTargetAccessCheck ForStatic(ITenantRepository repository, RtSecurityContext caller,
        IRtDataPermissionEvaluator evaluator, RtCkId<CkTypeId> ckTypeId) =>
        async (dataAccess, cancellationToken) => Map(await evaluator.EvaluateTypeAsync(repository, caller, ckTypeId,
            ActionOf(dataAccess), publishAudit: true, cancellationToken).ConfigureAwait(false));

    internal static RtDataAction ActionOf(CkMethodDataAccessDto dataAccess) =>
        dataAccess == CkMethodDataAccessDto.Read ? RtDataAction.Read : RtDataAction.Write;

    internal static CkMethodTargetAccess Map(RtDataPermissionVerdict verdict) => verdict.Decision switch
    {
        RtDataPermissionDecision.Allowed => new CkMethodTargetAccess(CkMethodTargetAccessOutcome.Allowed,
            verdict.AuditOnlyViolation),
        RtDataPermissionDecision.NotVisible => CkMethodTargetAccess.NotVisible,
        // Anything else, also a value a later engine adds, refuses.
        _ => CkMethodTargetAccess.NotPermitted
    };
}
