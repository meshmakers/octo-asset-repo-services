using System.Security.Claims;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Files.Generated.System.Files.v1;
using Meshmakers.Octo.Services.Infrastructure.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     Fixture of the platform file system tests (AB#6171): the GraphQL fixture plus System.Files imported
///     into the system tenant and the asset repository's service migrations run (default root "Files"),
///     exactly as the tenant setup does it.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class FilesTestFixture : GraphQlTestFixture
{
    public FilesTestFixture()
    {
        // Last registration wins over the engine's TryAddSingleton: tests switch data policies on and off.
        Services.AddSingleton<Meshmakers.Octo.Runtime.Contracts.DataPermissions.IDataPermissionResolver>(Permissions);
    }

    /// <summary>
    ///     Data-policy table of the tenant; tests set it and reset it to empty in a finally block.
    /// </summary>
    public TestDataPermissionResolver Permissions { get; } = new();

    /// <summary>
    ///     A user principal with the role FileManagement.
    /// </summary>
    public static ClaimsPrincipal FileManager { get; } = CreateUser("file-manager", "FileManagement");

    /// <summary>
    ///     A user principal without file roles.
    /// </summary>
    public static ClaimsPrincipal PlainUser { get; } = CreateUser("plain-user");

    public static ClaimsPrincipal CreateUser(string subject, params string[] roles)
    {
        var claims = new List<Claim> { new("sub", subject), new(ClaimTypes.Name, subject) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "IntegrationTests"));
    }

    public FileSystemService FileSystem => GetService<FileSystemService>();

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        var systemContext = GetSystemContext();
        var operationResult = new OperationResult();
        await systemContext.ImportCkModelAsync(SystemFilesCkIds.CkModelId, operationResult);
        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            throw new InvalidOperationException("Failed to import System.Files: " +
                                                operationResult.GetMessages());
        }

        await RunServiceMigrationsAsync();
    }

    /// <summary>
    ///     Number of GridFS files with the given id in the system tenant database.
    /// </summary>
    public async Task<long> CountGridFsFilesAsync(string binaryId)
    {
        var database = new MongoDB.Driver.MongoClient(GetConnectionString()).GetDatabase(SystemDatabaseName);
        return await database.GetCollection<MongoDB.Bson.BsonDocument>("fs.files")
            .CountDocumentsAsync(new MongoDB.Bson.BsonDocument("_id", MongoDB.Bson.ObjectId.Parse(binaryId)));
    }

    /// <summary>
    ///     Runs the asset repository's service migrations against the system tenant (idempotent).
    /// </summary>
    public async Task RunServiceMigrationsAsync()
    {
        var systemContext = GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await GetService<MigrationService>().ExecuteMigrationsAsync(session, systemContext);
        await session.CommitTransactionAsync();
    }
}

/// <summary>
///     Resolver stub with a mutable policy table (same approach as the engine's DataPermissionTestFixture).
/// </summary>
public sealed class TestDataPermissionResolver : Meshmakers.Octo.Runtime.Contracts.DataPermissions.IDataPermissionResolver
{
    public Meshmakers.Octo.Runtime.Contracts.DataPermissions.RtDataPolicyTable Table { get; set; } =
        Meshmakers.Octo.Runtime.Contracts.DataPermissions.RtDataPolicyTable.Empty;

    public Task<Meshmakers.Octo.Runtime.Contracts.DataPermissions.RtDataPolicyTable> GetPolicyTableAsync(
        Meshmakers.Octo.Runtime.Contracts.Repositories.IRuntimeRepository runtimeRepository) => Task.FromResult(Table);

    public void Invalidate(string tenantId)
    {
    }
}
