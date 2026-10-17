using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AssetRepositoryServices.UnitTests.Configuration;

/// <summary>
///     AB#6832 (Lane B of AB#6728) — pins that keep the LogOnly -> Enforce flip a pure configuration
///     change: the registration order in the real <c>Program.cs</c>, the operator-settable names of the
///     mode, and the absence of the parent-tenant marker on this service's endpoints.
/// </summary>
public class ProgramTenantGatePinTests
{
    private const string EnvironmentVariable = "OCTO_TENANTAUTHORIZATION__USERTOKENENFORCEMENT";

    // ---- Program.cs source pins ------------------------------------------------------------------

    /// <summary>
    ///     🔴 The LogOnly opt-down must be registered BEFORE <c>AddOctoTenantAuthorization(builder.Configuration)</c>.
    ///     Options configurators run in registration order, so with the order reversed the code default
    ///     would win and the helm value would be inert — silently, with the gate wired and green.
    ///     (The wiring tests re-state the two calls; this one reads the real file.)
    /// </summary>
    [Fact]
    public void ProgramCs_RegistersTheLogOnlyOptDownBeforeTheConfigurationBinding()
    {
        var code = ProgramCode();

        var optDown = Regex.Match(code,
            @"AddOctoTenantAuthorization\s*\(\s*o\s*=>\s*o\.UserTokenEnforcement\s*=\s*UserTokenTenantEnforcementMode\.LogOnly\s*\)");
        var binding = Regex.Match(code, @"AddOctoTenantAuthorization\s*\(\s*builder\.Configuration\s*\)");

        optDown.Success.Should().BeTrue("the user-token gate starts in LogOnly in code (AB#5054)");
        binding.Success.Should().BeTrue("the section binding is what makes OCTO_TENANTAUTHORIZATION__… work (AB#5047)");
        optDown.Index.Should().BeLessThan(binding.Index,
            "the code default must come first so that configuration wins");
        Regex.Matches(code, @"AddOctoTenantAuthorization\s*\(").Count
            .Should().Be(2, "a third registration could re-set the mode after the configuration binding");
    }

    /// <summary>
    ///     Environment variables must be part of the configuration, with the prefix the helm chart and the
    ///     runbook use (<c>OCTO_</c>), and be added before the section is bound from it.
    /// </summary>
    [Fact]
    public void ProgramCs_ReadsEnvironmentVariablesWithTheOctoPrefixBeforeBindingTheGate()
    {
        var code = ProgramCode();

        var env = Regex.Match(code, @"AddEnvironmentVariables\s*\(\s*""OCTO_""\s*\)");
        var binding = Regex.Match(code, @"AddOctoTenantAuthorization\s*\(\s*builder\.Configuration\s*\)");

        env.Success.Should().BeTrue();
        env.Index.Should().BeLessThan(binding.Index);
    }

    /// <summary>
    ///     The pipeline order that makes the middleware see an authenticated principal and a resolved route:
    ///     routing, authentication, authorization, then the tenant gate.
    /// </summary>
    [Fact]
    public void PipelineExtension_RunsTheTenantGateAfterRoutingAuthenticationAndAuthorization()
    {
        var code = StripComments(File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "AssetRepositoryServices",
            "Configuration", "OctoApplicationBuilderExtensions.cs")));

        var order = new[] { "app.UseRouting()", "app.UseAuthentication()", "app.UseAuthorization()",
                "app.UseOctoTenantAuthorization()", "app.MapControllers()" }
            .Select(call => code.IndexOf(call, StringComparison.Ordinal)).ToArray();

        order.Should().OnlyContain(i => i >= 0, "every call must be present");
        order.Should().BeInAscendingOrder();
    }

    // ---- the mode is operator-settable through the real names ------------------------------------

    [Fact]
    public void Enforce_WinsOverTheLogOnlyCodeDefault_FromTheConfigurationSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TenantAuthorization:UserTokenEnforcement"] = "Enforce"
            })
            .Build();

        ResolveMode(configuration).Should().Be(UserTokenTenantEnforcementMode.Enforce);
    }

    /// <summary>
    ///     The real variable name of the helm value, read by the same provider call as in Program.cs.
    ///     Process-wide state, so it is set and restored inside the test; the name is specific to this
    ///     setting and read by no other test.
    /// </summary>
    [Fact]
    public void Enforce_WinsOverTheLogOnlyCodeDefault_FromTheEnvironmentVariable()
    {
        var previous = Environment.GetEnvironmentVariable(EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(EnvironmentVariable, "Enforce");
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables("OCTO_").Build();

            ResolveMode(configuration).Should().Be(UserTokenTenantEnforcementMode.Enforce);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvironmentVariable, previous);
        }
    }

    [Fact]
    public void WithoutConfiguration_TheServiceStaysInLogOnly()
    {
        ResolveMode(new ConfigurationBuilder().Build()).Should().Be(UserTokenTenantEnforcementMode.LogOnly);
    }

    // ---- no endpoint carries the parent-tenant marker --------------------------------------------

    /// <summary>
    ///     A marker on a GraphQL or data route would give a parent tenant the data of its children
    ///     (AB#5060). No controller or action of this service may carry it, directly or through a custom
    ///     attribute implementing <see cref="IAllowParentTenantAdministration" />.
    /// </summary>
    [Fact]
    public void NoControllerOrActionCarriesTheParentTenantMarker()
    {
        var assembly = typeof(Meshmakers.Octo.Backend.AssetRepositoryServices.Program).Assembly;
        var marked = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            if (HasMarker(type))
            {
                marked.Add(type.FullName!);
            }

            foreach (var member in type.GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                                   BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (HasMarker(member))
                {
                    marked.Add($"{type.FullName}.{member.Name}");
                }
            }
        }

        marked.Should().BeEmpty("no endpoint of asset-repo may be exempt for parent tenants");

        // Sanity: the scan does see this service's controllers (a vacuous pass would prove nothing).
        assembly.GetTypes().Count(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .Should().BeGreaterThan(0);
    }

    /// <summary>
    ///     The GraphQL endpoints are mapped, not attributed: pin that their mapping carries no marker
    ///     metadata either.
    /// </summary>
    [Fact]
    public void NoEndpointMappingInTheSourceReferencesTheParentTenantMarker()
    {
        var offenders = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), "src", "AssetRepositoryServices"), "*.cs",
                SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => Regex.IsMatch(StripComments(File.ReadAllText(f)), @"AllowParentTenantAdministration"))
            .Select(Path.GetFileName)
            .ToList();

        offenders.Should().BeEmpty("the parent-tenant marker must not appear in code of this service");
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static bool HasMarker(MemberInfo member)
    {
        return member.GetCustomAttributes(true).Any(a => a is IAllowParentTenantAdministration);
    }

    /// <summary>The two registrations of Program.cs applied to a given configuration, in that order.</summary>
    private static UserTokenTenantEnforcementMode ResolveMode(IConfiguration configuration)
    {
        var provider = new ServiceCollection()
            .AddOctoTenantAuthorization(o => o.UserTokenEnforcement = UserTokenTenantEnforcementMode.LogOnly)
            .AddOctoTenantAuthorization(configuration)
            .BuildServiceProvider();

        return provider.GetRequiredService<IOptions<TenantAuthorizationOptions>>().Value.UserTokenEnforcement;
    }

    private static string ProgramCode()
    {
        return StripComments(File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "AssetRepositoryServices",
            "Program.cs")));
    }

    /// <summary>Comments describe the very calls these pins look for, so they are stripped first.</summary>
    private static string StripComments(string source)
    {
        return Regex.Replace(source, @"//.*?$", string.Empty, RegexOptions.Multiline);
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
    }
}
