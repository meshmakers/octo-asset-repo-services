using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Exchange;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
/// Fixture that imports the AssetRepositoryIntegrationTest construction kit model
/// and loads sample runtime data for testing.
/// Uses the test tenant for all data.
/// </summary>
public class SampleDataFixture : AssetRepoFixture
{
    private const string RtModelPath = "testData/sampleRtModel.yaml";

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        // Get system context
        var systemContext = GetSystemContext();

        // Import AssetRepositoryIntegrationTest Construction Kit Model
        var demoOperationResult = new OperationResult();
        await systemContext.ImportCkModelAsync(new CkModelId("AssetRepositoryIntegrationTest"), demoOperationResult);

        if (demoOperationResult.HasErrors || demoOperationResult.HasFatalErrors)
        {
            throw new InvalidOperationException("Failed to import AssetRepositoryIntegrationTest CK model");
        }

        // Import Sample Runtime Data
        var importRtModelCommand = GetService<IImportRtModelCommand>();
        var repository = systemContext.GetSystemTenantRepository();

        await importRtModelCommand.ImportAsync(
            repository,
            RtModelPath,
            ExchangeMimeTypes.MimeTypeYaml,
            ImportStrategy.Insert);
    }
}
