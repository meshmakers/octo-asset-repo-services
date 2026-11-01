using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

/// <summary>
///     AB#8042: the generic GraphQL write paths refuse the identity-owned fields of <c>System.Identity/User</c> and the edges of
///     the identity association roles (<see cref="IdentityOwnedWrites" />, the one list in the engine) for every caller, so a
///     caller with generic write rights cannot bypass the identity REST API and methods (role-change guard, audit, 2FA, external
///     logins). Covered: <c>runtime.runtimeEntities.create/update</c> and the typed <c>runtime.&lt;type&gt;.create/update</c>
///     (attribute input and association input, both directions) and the query-row create/update (cell paths and navigation
///     associations). The refusal is <c>ATTRIBUTE_NOT_WRITABLE</c> with the extension <c>reason = identity-owned</c>
///     (<see cref="HiddenAttributeAccessException.IdentityOwned" />); nothing is written.
/// </summary>
/// <remarks>
///     Interim (decision Gerald 2026-11-01, option B): the attributes move into the CK <c>access</c> of System.Identity with its
///     next major (together with AB#7777); the association check stays until the CK model can restrict association writes.
///     Writes of the identity service through its own repository, blueprint seeds and tenant restores never pass here.
/// </remarks>
internal static class IdentityOwnedWriteGuard
{
    /// <summary>Refuses an attribute of a GraphQL entity input (the attribute name as declared on the type).</summary>
    internal static void EnsureAttributeWritable(CkTypeWithAttributesGraph graph, string attributeName, string entityName)
    {
        if (graph is CkTypeGraph type &&
            IdentityOwnedWrites.IsIdentityOwnedAttribute(type.CkTypeId.ToRtCkId(), attributeName))
        {
            throw HiddenAttributeAccessException.IdentityOwned(attributeName, entityName);
        }
    }

    /// <summary>
    ///     Refuses a query-row cell path whose first segment is an identity-owned attribute of the row's type (a record member
    ///     path like <c>claims.value</c> is covered by its first segment). Navigation paths (<c>a-&gt;b</c>) write associations
    ///     and are judged by <see cref="EnsureAssociationsWritable" />.
    /// </summary>
    internal static void EnsureWritablePath(RtCkId<CkTypeId> ckTypeId, string attributePath)
    {
        if (string.IsNullOrEmpty(attributePath) || attributePath.Contains("->"))
        {
            return;
        }

        var firstSegment = attributePath.Split('.', 2)[0];
        var bracket = firstSegment.IndexOf('[');
        if (bracket >= 0)
        {
            firstSegment = firstSegment[..bracket];
        }

        if (IdentityOwnedWrites.IsIdentityOwnedAttribute(ckTypeId, firstSegment))
        {
            throw HiddenAttributeAccessException.IdentityOwned(attributePath, ckTypeId.ToString());
        }
    }

    /// <summary>Refuses every association change (insert or delete) whose role is an identity association role.</summary>
    internal static void EnsureAssociationsWritable(IEnumerable<AssociationUpdateInfo> associations)
    {
        var offender = associations.FirstOrDefault(a => IdentityOwnedWrites.IsIdentityOwnedAssociation(a.RoleId));
        if (offender != null)
        {
            throw HiddenAttributeAccessException.IdentityOwned(offender.RoleId.FullName,
                offender.Origin.CkTypeId.ToString());
        }
    }
}
