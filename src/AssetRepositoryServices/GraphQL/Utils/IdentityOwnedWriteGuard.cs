using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

/// <summary>
///     AB#8042 / AB#8053: the generic GraphQL write paths refuse the edges of the identity association roles
///     (<see cref="IdentityOwnedWrites" />, the one list in the engine) for every caller, so a caller with generic write rights
///     cannot bypass the identity REST API and methods (role-change guard, audit, group membership). Covered:
///     <c>runtime.runtimeEntities.create/update</c> and the typed <c>runtime.&lt;type&gt;.create/update</c> (association input,
///     both directions) and the query-row create/update (navigation associations). The refusal is <c>ATTRIBUTE_NOT_WRITABLE</c>
///     with the extension <c>reason = identity-owned</c> (<see cref="HiddenAttributeAccessException.IdentityOwned" />); nothing
///     is written.
/// </summary>
/// <remarks>
///     The identity-owned attributes of <c>System.Identity/User</c> are no longer listed here: since System.Identity 3.0.0 they are
///     <c>access: MethodOnly</c> in the model and the generic access guard (<c>AttributeAccess.IsGenericallyWritable</c>,
///     <c>AccessQueryGuard</c>) refuses them. The association check stays because the CK access model has no association-level
///     write restriction (<c>access</c> exists only on attributes). Writes of the identity service through its own repository, blueprint seeds and tenant restores never pass here.
/// </remarks>
internal static class IdentityOwnedWriteGuard
{
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
