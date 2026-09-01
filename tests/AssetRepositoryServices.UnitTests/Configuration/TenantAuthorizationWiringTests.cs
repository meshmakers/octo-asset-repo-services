using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AssetRepositoryServices.UnitTests.Configuration;

/// <summary>
///     AB#5054 — pins the wiring that makes the shared transport tenant gate
///     (<c>UseOctoTenantAuthorization()</c> / <c>TenantAuthorizationMiddleware</c>) effective in this
///     service, and the migration mode it starts in. Every piece here fails <i>silently</i>: drop
///     one and the gate goes back to letting every request through, with no compile error and no
///     other test turning red.
/// </summary>
public class TenantAuthorizationWiringTests
{
    private static JwtBearerOptions Configure(string authority = "https://localhost:5003")
    {
        var options = new JwtBearerOptions();
        new ConfigureJwtBearerOptions(
                Options.Create(new OctoAssetRepositoryServicesOptions { Authority = authority }))
            .Configure(options);
        return options;
    }

    /// <summary>
    ///     🔴 The silent-no-op trap. The middleware skips any principal whose
    ///     <c>AuthenticationType</c> is not <c>Bearer</c> — a guard against false 403s on the
    ///     cookie principal this service issues for the GraphQL playground. The JWT handler's
    ///     default label is <c>AuthenticationTypes.Federation</c>, so without this the gate never
    ///     fires on a bearer request. It matters most here: the service runs with
    ///     <c>ValidateAudience = false</c>, so the tenant match is the only transport-level barrier
    ///     between a client-credentials client of the authority and a foreign tenant.
    /// </summary>
    [Fact]
    public void ConfigureJwtBearerOptions_LabelsTheIdentityBearer()
    {
        Configure().TokenValidationParameters.AuthenticationType
            .Should().Be(JwtBearerDefaults.AuthenticationScheme);
    }

    /// <summary>
    ///     The settings the extracted configurator took over from the former inline delegate.
    /// </summary>
    [Fact]
    public void ConfigureJwtBearerOptions_KeepsAuthorityIssuerAndAudienceContract()
    {
        var options = Configure("https://identity.example.com");

        options.Authority.Should().Be("https://identity.example.com/");
        // Trailing slash: IdentityServer stamps `iss` with one, so ValidIssuer must match exactly.
        options.TokenValidationParameters.ValidIssuer.Should().Be("https://identity.example.com/");
        options.TokenValidationParameters.ValidateAudience.Should().BeFalse();
    }

    /// <summary>
    ///     The user path is armed in stages (AB#5054): the gate has never run here, and a known
    ///     cross-tenant caller exists (meshmakers-app queries this service's GraphQL for the tenant
    ///     topology with the user's own token against the root tenant's route). LogOnly writes the
    ///     inventory without changing any outcome. The platform default is <c>Enforce</c>, so this
    ///     opt-down has to be explicit — and it must be overridable, hence the ordering test below.
    /// </summary>
    [Fact]
    public void UserTokenEnforcement_StartsInTheMigrationMode()
    {
        var provider = new ServiceCollection()
            .AddOctoTenantAuthorization(o => o.UserTokenEnforcement = UserTokenTenantEnforcementMode.LogOnly)
            .AddOctoTenantAuthorization(new ConfigurationBuilder().Build())
            .BuildServiceProvider();

        provider.GetRequiredService<IOptions<TenantAuthorizationOptions>>().Value
            .UserTokenEnforcement.Should().Be(UserTokenTenantEnforcementMode.LogOnly);
    }

    /// <summary>
    ///     🔴 Registration order is load-bearing: the code default must come BEFORE the section
    ///     binding, otherwise <c>OCTO_TENANTAUTHORIZATION__USERTOKENENFORCEMENT=Enforce</c> is inert
    ///     and the service is stuck in the migration mode while the estate moves on — the exact
    ///     class of silent outlier AB#5047 had to fix once already.
    /// </summary>
    [Fact]
    public void UserTokenEnforcement_IsStillOperatorSettable()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TenantAuthorization:UserTokenEnforcement"] = "Enforce"
            })
            .Build();

        var provider = new ServiceCollection()
            .AddOctoTenantAuthorization(o => o.UserTokenEnforcement = UserTokenTenantEnforcementMode.LogOnly)
            .AddOctoTenantAuthorization(configuration)
            .BuildServiceProvider();

        provider.GetRequiredService<IOptions<TenantAuthorizationOptions>>().Value
            .UserTokenEnforcement.Should().Be(UserTokenTenantEnforcementMode.Enforce);
    }

    /// <summary>
    ///     🔴 The test above proves nothing on its own — and that is not a figure of speech.
    ///     octo-ai-services had exactly that test, green, while the label was wiped at runtime: its
    ///     <c>Program.cs</c> configured the bearer scheme a <b>second</b> time via
    ///     <c>AddJwtBearer(jwt =&gt; { jwt.TokenValidationParameters = new TokenValidationParameters
    ///     { … }; })</c>. The options factory runs configurators in registration order, so the later
    ///     delegate replaced the whole instance — label and <c>ValidIssuer</c> gone — and the gate
    ///     was a no-op for a full release (AB#5051 → AB#5056).
    ///     <para>
    ///         There is no way to resolve the composed <c>JwtBearerOptions</c> of this host from a
    ///         unit test (the registration lives in a private method that needs the whole runtime
    ///         engine), so this guard pins the composition rule at the source instead: exactly one
    ///         configurator owns the scheme, and <c>AddJwtBearer</c> is called without an argument.
    ///     </para>
    /// </summary>
    [Fact]
    public void ConfigureJwtBearerOptions_IsTheOnlyConfiguratorOfTheBearerScheme()
    {
        var registration = File.ReadAllText(Path.Combine(RepositoryRoot(),
            "src", "AssetRepositoryServices", "Configuration", "DependencyInjection",
            "RuntimeEngineBuilderExtensions.cs"));

        registration.Should().Contain("ConfigureOptions<ConfigureJwtBearerOptions>()",
            "the configurator that sets the Bearer AuthenticationType must be registered");

        // Comments talk about the very pattern this guards against, so strip them first.
        var code = Regex.Replace(registration, @"//.*?$", string.Empty, RegexOptions.Multiline);

        Regex.Matches(code, @"AddJwtBearer\s*\(\s*[^)\s]")
            .Should().BeEmpty(
                "a configuration delegate on AddJwtBearer runs AFTER ConfigureJwtBearerOptions and " +
                "can silently discard the AuthenticationType the tenant gate depends on — put the " +
                "setting into ConfigureJwtBearerOptions instead (AB#5054)");
    }

    /// <summary>
    ///     Repository root, derived from this file's compile-time path so it is independent of the
    ///     build output directory.
    /// </summary>
    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
    }
}
