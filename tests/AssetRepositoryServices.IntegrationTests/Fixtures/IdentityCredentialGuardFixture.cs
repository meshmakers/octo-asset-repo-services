using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     Credential guard regression tests: the GraphQL host of <see cref="MethodGatewayGraphQlTestFixture" /> (real schema,
///     gateway, MongoDB, scripted dispatcher, log capture) with the real <c>System.Identity</c> model
///     (<see cref="SystemIdentitySnapshotCkModel" />) and one user whose credential attributes hold known dummy values.
///     The identity service writes those attributes through the repository; the fixture does the same, every other path is
///     the one under test.
/// </summary>
public class IdentityCredentialGuardFixture : MethodGatewayGraphQlTestFixture
{
    /// <summary>Fixed test constants - never real credentials. No response, export, file or log may contain them.</summary>
    public const string DummyPasswordHash = "DUMMY-HASH-7781";

    public const string DummySecurityStamp = "DUMMY-STAMP-7781";

    public const string DummyTokenValue = "DUMMY-TOKEN-7781";

    public static readonly string[] DummyValues = [DummyPasswordHash, DummySecurityStamp, DummyTokenValue];

    public const string UserCkTypeId = "System.Identity/User";

    public const string UserTokenCkRecordId = "System.Identity/UserToken";

    public const string RoleCkTypeId = "System.Identity/Role";

    public const string AssignedRoleId = "System.Identity/AssignedRole";

    /// <summary>GraphQL type and field names of the user.</summary>
    public const string UserTypeName = "SystemIdentityUser";

    public const string UserQueryField = "systemIdentityUser";

    public const string UserMutationField = "systemIdentityUsers";

    /// <summary>MongoDB collection suffix of the user (collection root).</summary>
    public const string UserCollectionSuffix = "SystemIdentityUser";

    public const string ProbeUserName = "probe-user-7781";

    public const string ProbeUserEmail = "probe-user-7781@example.invalid";

    public IdentityCredentialGuardFixture()
    {
        Services.AddSystemIdentitySnapshotCkModel();
    }

    /// <summary>RtId of the seeded user.</summary>
    public string ProbeUserRtId { get; private set; } = string.Empty;

    /// <summary>RtId of the role assigned to the last seeded user.</summary>
    public string ProbeRoleRtId { get; private set; } = string.Empty;

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        var systemContext = GetSystemContext();
        var result = new OperationResult();
        await systemContext.ImportCkModelAsync(new CkModelId(SystemIdentitySnapshotCkModel.ModelFullName), result);
        if (result.HasErrors || result.HasFatalErrors)
        {
            throw new InvalidOperationException("Failed to import System.Identity: " + result.GetMessages());
        }

        ProbeUserRtId = await SeedUserAsync(ProbeUserName, ProbeUserEmail);
    }

    /// <summary>
    ///     Inserts a user with dummy credentials through the repository - the way the identity store writes them.
    /// </summary>
    public async Task<string> SeedUserAsync(string userName, string email)
    {
        var repository = GetSystemContext().GetSystemTenantRepository();
        var ckTypeId = new RtCkId<CkTypeId>(UserCkTypeId);
        var user = await repository.CreateTransientRtEntityByRtCkIdAsync(ckTypeId);
        user.SetAttributeValue("UserName", AttributeValueTypesDto.String, userName);
        user.SetAttributeValue("NormalizedUserName", AttributeValueTypesDto.String, userName.ToUpperInvariant());
        user.SetAttributeValue("Email", AttributeValueTypesDto.String, email);
        user.SetAttributeValue("NormalizedEmail", AttributeValueTypesDto.String, email.ToUpperInvariant());
        user.SetAttributeValue("EmailConfirmed", AttributeValueTypesDto.Boolean, true);
        user.SetAttributeValue("PhoneNumberConfirmed", AttributeValueTypesDto.Boolean, false);
        user.SetAttributeValue("TwoFactorEnabled", AttributeValueTypesDto.Boolean, false);
        user.SetAttributeValue("LockoutEnabled", AttributeValueTypesDto.Boolean, true);
        user.SetAttributeValue("AccessFailedCount", AttributeValueTypesDto.Integer, 3);
        user.SetAttributeValue("ResetPasswordOnLogin", AttributeValueTypesDto.Boolean, false);
        user.SetAttributeValue("PasswordHash", AttributeValueTypesDto.String, DummyPasswordHash);
        user.SetAttributeValue("SecurityStamp", AttributeValueTypesDto.String, DummySecurityStamp);
        user.SetAttributeValue("UserTokens", AttributeValueTypesDto.RecordArray, new List<RtRecord>
        {
            new(new RtCkId<CkRecordId>(UserTokenCkRecordId), new Dictionary<string, object?>
            {
                ["LoginProvider"] = "[AspNetUserStore]",
                ["Name"] = "AuthenticatorKey",
                ["UserId"] = userName,
                ["Value"] = DummyTokenValue
            })
        });

        // A role assigned to the user: the inbound navigation Role.assignedEntities is the path of the entity-selector probes.
        var roleCkTypeId = new RtCkId<CkTypeId>(RoleCkTypeId);
        var role = await repository.CreateTransientRtEntityByRtCkIdAsync(roleCkTypeId);
        role.SetAttributeValue("Name", AttributeValueTypesDto.String, "ProbeRole-" + userName);
        role.SetAttributeValue("NormalizedName", AttributeValueTypesDto.String, ("ProbeRole-" + userName).ToUpperInvariant());

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var result = new OperationResult();
        await repository.ApplyChangesAsync(session,
            [EntityUpdateInfo<RtEntity>.CreateInsert(ckTypeId, user), EntityUpdateInfo<RtEntity>.CreateInsert(roleCkTypeId, role)],
            [AssociationUpdateInfo.CreateInsert(new RtEntityId(ckTypeId, user.RtId), new RtEntityId(roleCkTypeId, role.RtId),
                new RtCkId<CkAssociationRoleId>(AssignedRoleId))], result);
        if (result.HasErrors || result.HasFatalErrors)
        {
            throw new InvalidOperationException("Seeding the probe user failed: " + result.GetMessages());
        }

        await session.CommitTransactionAsync();
        ProbeRoleRtId = role.RtId.ToString();
        return user.RtId.ToString();
    }

    /// <summary>Number of captured log lines so far; tests of one collection run one after the other.</summary>
    public int LogCount
    {
        get
        {
            lock (Logs)
            {
                return Logs.Count;
            }
        }
    }

    /// <summary>The log lines captured after <paramref name="start" /> that contain a dummy value (should be empty).</summary>
    public IReadOnlyList<string> LogLinesWithDummyValuesSince(int start)
    {
        lock (Logs)
        {
            return Logs.Skip(start).Select(l => l.Text)
                .Where(text => DummyValues.Any(d => text.Contains(d, StringComparison.Ordinal)))
                .ToList();
        }
    }
}
