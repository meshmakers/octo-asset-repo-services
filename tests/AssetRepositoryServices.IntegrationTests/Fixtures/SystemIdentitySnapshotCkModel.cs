using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     The real <c>System.Identity</c> CK model, as an embedded model of the test host. The asset repository does not ship
///     System.Identity (the identity service owns it), so the credential guard tests load the compiler output of a
///     released version from <c>testData/ck-models/</c> (provenance and refresh: README.md there). The guards under test are
///     generic; what this model contributes is the real access matrix of the user (Hidden <c>PasswordHash</c>,
///     <c>SecurityStamp</c>, <c>UserTokens</c>; MethodOnly <c>UserName</c>, <c>Email</c> and, since System.Identity 3.0.0
///     (AB#8053), the identity-owned fields <c>TwoFactorEnabled</c>, <c>LockoutEnd</c>, <c>Claims</c> ...) and its methods,
///     among them the <c>replacesGenericCreate</c> flag of <c>CreateUser-1</c>.
/// </summary>
public sealed class SystemIdentitySnapshotCkModel(ICkYamlSerializer ckYamlSerializer) : ICkEmbeddedModel
{
    /// <summary>The model version of the snapshot.</summary>
    public const string ModelFullName = "System.Identity-3.0.0";

    private static readonly string SnapshotPath =
        Path.Combine(AppContext.BaseDirectory, "testData", "ck-models", "ck-system.identity-3.0.0.yaml");

    public CkModelId ModelId => new(ModelFullName);

    public string Description => "System.Identity (test snapshot of the identity service's CK model)";

    public async Task<CkCompiledModelRoot> GetCompiledModelRootAsync(OperationResult operationResult)
    {
        await using var stream = File.OpenRead(SnapshotPath);
        return await ckYamlSerializer.DeserializeCompiledModelRootAsync(stream, SnapshotPath, operationResult);
    }
}

public static class SystemIdentitySnapshotCkModelExtensions
{
    /// <summary>Registers the System.Identity snapshot in the embedded catalog of the test host.</summary>
    public static IServiceCollection AddSystemIdentitySnapshotCkModel(this IServiceCollection services)
    {
        services.AddTransient<ICkEmbeddedModel, SystemIdentitySnapshotCkModel>();
        return services;
    }
}
