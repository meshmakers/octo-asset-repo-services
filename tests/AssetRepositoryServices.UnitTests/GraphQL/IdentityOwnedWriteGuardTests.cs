using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     AB#8042: <see cref="IdentityOwnedWriteGuard" /> - the query-row path parsing and the association check. The attribute check of
///     the entity mutations and every write path are covered against the real System.Identity model in
///     <c>IntegrationTests/Security/IdentityCredentialGuardGraphQlTests</c>.
/// </summary>
public class IdentityOwnedWriteGuardTests
{
    private static readonly RtCkId<CkTypeId> User = new("System.Identity/User");
    private static readonly RtCkId<CkTypeId> Role = new("System.Identity/Role");

    [Theory]
    [InlineData("twoFactorEnabled")]
    [InlineData("TwoFactorEnabled")]
    [InlineData("claims.value")]
    [InlineData("userLogins[0].loginProvider")]
    public void QueryRowPath_OnAnIdentityOwnedUserField_IsRefused_WithTheReason(string path)
    {
        var act = () => IdentityOwnedWriteGuard.EnsureWritablePath(User, path);

        var exception = act.Should().Throw<HiddenAttributeAccessException>().Which;
        exception.Code.Should().Be("ATTRIBUTE_NOT_WRITABLE");
        exception.Reason.Should().Be("identity-owned");
        exception.IsWrite.Should().BeTrue();
    }

    [Theory]
    [InlineData("firstName")]
    [InlineData("assignedRoles->name")]
    [InlineData("")]
    public void QueryRowPath_OtherFieldsOrNavigation_PassesTheGuard(string path)
    {
        var act = () => IdentityOwnedWriteGuard.EnsureWritablePath(User, path);

        act.Should().NotThrow();
    }

    [Fact]
    public void QueryRowPath_OnAnotherType_PassesTheGuard()
    {
        var act = () => IdentityOwnedWriteGuard.EnsureWritablePath(Role, "twoFactorEnabled");

        act.Should().NotThrow();
    }

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
