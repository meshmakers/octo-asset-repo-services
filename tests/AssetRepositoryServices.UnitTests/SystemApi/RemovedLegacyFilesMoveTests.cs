using System.Reflection;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace AssetRepositoryServices.UnitTests.SystemApi;

/// <summary>
///     AB#7033: the one-time System.Reporting -> System.Files migration (sweep, status endpoint) was removed after it
///     had run on every cluster. These tests pin that no route <c>system/v1/files/migration-status/{tenantId}</c> is
///     mapped any more (the request answers 404) and that no type of the former migration namespace is left.
/// </summary>
public class RemovedLegacyFilesMoveTests
{
    private static readonly Assembly ServiceAssembly = typeof(FilesOrphanSweepController).Assembly;

    [Fact]
    public void NoControllerAction_MapsTheMigrationStatusRoute()
    {
        var templates = ServiceAssembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(GetRouteTemplates)
            .ToList();

        templates.Should().NotBeEmpty("the reflection must see the controllers of the service");
        templates.Should().NotContain(t => t.Contains("migration-status", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FilesSystemApi_StillMapsTheOrphanSweep()
    {
        GetRouteTemplates(typeof(FilesOrphanSweepController))
            .Should().Contain(t => t.Contains("files/orphan-sweep", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoType_OfTheFormerMigrationNamespace_IsLeft()
    {
        ServiceAssembly.GetTypes()
            .Select(t => t.Namespace ?? string.Empty)
            .Should().NotContain(ns => ns.EndsWith(".Services.Files.Migration", StringComparison.Ordinal));
    }

    private static IEnumerable<string> GetRouteTemplates(Type controller)
    {
        var prefixes = controller.GetCustomAttributes<RouteAttribute>(true).Select(r => r.Template).ToList();
        if (prefixes.Count == 0)
        {
            prefixes.Add(string.Empty);
        }

        foreach (var method in controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        {
            foreach (var http in method.GetCustomAttributes<HttpMethodAttribute>(true))
            {
                foreach (var prefix in prefixes)
                {
                    yield return $"{prefix}/{http.Template}";
                }
            }
        }
    }
}
