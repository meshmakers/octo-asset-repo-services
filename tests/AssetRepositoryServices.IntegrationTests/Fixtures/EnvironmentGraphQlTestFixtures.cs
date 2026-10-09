using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;

/// <summary>
///     F1.5-S4 (AB#5923): a GraphQL fixture whose host runs in the <c>Production</c> environment, so the real
///     error-info configuration (<c>GraphQlErrorExposure</c>, AB#6189) decides about exception details.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class ProductionGraphQlTestFixture : GraphQlTestFixture
{
    public ProductionGraphQlTestFixture()
    {
        Services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Production));
    }
}

/// <summary>
///     F1.5-S4 (AB#5923): the same in the <c>Development</c> environment (diagnostics stay available).
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class DevelopmentGraphQlTestFixture : GraphQlTestFixture
{
    public DevelopmentGraphQlTestFixture()
    {
        Services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Development));
    }
}

internal sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "AssetRepositoryServices.IntegrationTests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
