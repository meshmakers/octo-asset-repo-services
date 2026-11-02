using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     AB#8042 / AB#8053: <see cref="IdentityOwnedWriteGuard" /> - the association check. The identity-owned User attributes are
///     <c>access: MethodOnly</c> in System.Identity 3.0.0 and judged by the generic access guards; they and every write path
///     are covered against the real System.Identity model in <c>IntegrationTests/Security/IdentityCredentialGuardGraphQlTests</c>.
/// </summary>
public class IdentityOwnedWriteGuardTests
{
    private static readonly RtCkId<CkTypeId> User = new("System.Identity/User");
    private static readonly RtCkId<CkTypeId> Role = new("System.Identity/Role");

    [Theory]
    [InlineData("System.Identity/AssignedRole")]
    [InlineData("System.Identity/GroupMember")]
    [InlineData("System.Identity/ChildGroup")]
    [InlineData("System.Identity/MayActAs")]
    [InlineData("System.Identity/GrantsPermission")]
    public void IdentityAssociation_InsertOrDelete_IsRefused(string roleId)
    {
        var origin = new RtEntityId(User, OctoObjectId.GenerateNewId());
        var target = new RtEntityId(Role, OctoObjectId.GenerateNewId());

        foreach (var change in new[]
                 {
                     AssociationUpdateInfo.CreateInsert(origin, target, roleId),
                     AssociationUpdateInfo.CreateDelete(origin, target, roleId)
                 })
        {
            var act = () => IdentityOwnedWriteGuard.EnsureAssociationsWritable([change]);
            act.Should().Throw<HiddenAttributeAccessException>().Which.Reason.Should().Be("identity-owned");
        }
    }

    [Theory]
    [InlineData("System/ParentChild")]
    [InlineData("System.Identity/PolicyPermission")]
    [InlineData("System.Identity/IdentifiesUser")]
    public void OtherAssociations_PassTheGuard(string roleId)
    {
        var change = AssociationUpdateInfo.CreateInsert(new RtEntityId(User, OctoObjectId.GenerateNewId()),
            new RtEntityId(Role, OctoObjectId.GenerateNewId()), roleId);

        var act = () => IdentityOwnedWriteGuard.EnsureAssociationsWritable([change]);

        act.Should().NotThrow();
    }
}
