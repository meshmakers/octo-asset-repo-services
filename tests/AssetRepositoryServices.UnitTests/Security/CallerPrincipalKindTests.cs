using System.Security.Claims;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Security;
using Meshmakers.Octo.Runtime.Contracts;
using Xunit;
using Helpers = Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Helpers;

namespace AssetRepositoryServices.UnitTests.Security;

/// <summary>
///     AB#8705 — the principal kind derived from the caller's token: user tokens are Interactive, client-credentials,
///     delegated (on-behalf-of) and impersonated tokens are Client, a token without the distinguishing claims and an
///     unauthenticated caller are Unknown. Claim names are accepted in JWT short form and in the inbound-mapped form.
/// </summary>
public class CallerPrincipalKindTests
{
    private static ClaimsPrincipal Authenticated(params (string Type, string Value)[] claims)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Bearer"));
    }

    public static TheoryData<string, (string, string)[], RtPrincipalKind> Tokens => new()
    {
        // authorization code (Studio) and device code (octo-cli, MCP desktop): the user's subject
        { "interactive login", [("sub", "u1"), ("amr", "pwd")], RtPrincipalKind.Interactive },
        { "device code", [("sub", "u1"), ("amr", "mfa"), ("client_id", "octo-cli")], RtPrincipalKind.Interactive },
        { "mapped subject", [(ClaimTypes.NameIdentifier, "u1")], RtPrincipalKind.Interactive },
        // cross-tenant exchange of the person's own token: no actor, still the person
        { "token exchange", [("sub", "shadow-1"), ("amr", "token_exchange")], RtPrincipalKind.Interactive },
        // client credentials: no sub by platform contract
        { "client credentials", [("client_id", "svc")], RtPrincipalKind.Client },
        // on-behalf-of (mcpDelegateToCaller): user sub + act; Odin decision 2026-11-10 = Client
        { "delegated", [("sub", "u1"), ("act", "sa"), ("amr", "delegation")], RtPrincipalKind.Client },
        { "delegated, act only", [("sub", "u1"), ("act", "sa")], RtPrincipalKind.Client },
        { "delegated, amr only", [("sub", "u1"), ("amr", "delegation")], RtPrincipalKind.Client },
        { "delegated, mapped claims", [(ClaimTypes.NameIdentifier, "u1"), (ClaimTypes.Actor, "sa")],
            RtPrincipalKind.Client },
        { "delegated, mapped amr", [("sub", "u1"),
            ("http://schemas.microsoft.com/claims/authnmethodsreferences", "delegation")], RtPrincipalKind.Client },
        // impersonation: client-shaped for the target, act = actor
        { "impersonated", [("client_id", "target"), ("act", "actor"), ("amr", "impersonation")],
            RtPrincipalKind.Client },
        // neither sub nor client_id
        { "no distinguishing claim", [("scope", "assetSystemAPI"), ("role", "TenantManagement")],
            RtPrincipalKind.Unknown },
        { "blank subject", [("sub", " ")], RtPrincipalKind.Unknown }
    };

    [Theory]
    [MemberData(nameof(Tokens))]
    public void Resolve_MapsTheTokenShape(string shape, (string, string)[] claims, RtPrincipalKind expected)
    {
        CallerPrincipalKind.Resolve(Authenticated(claims)).Should().Be(expected, shape);
    }

    [Fact]
    public void Resolve_UnauthenticatedOrMissingCaller_IsUnknown()
    {
        CallerPrincipalKind.Resolve(null).Should().Be(RtPrincipalKind.Unknown);
        CallerPrincipalKind.Resolve(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u1")])))
            .Should().Be(RtPrincipalKind.Unknown, "an identity without authentication type is not authenticated");
    }

    [Fact]
    public void GetSecurityContext_CarriesThePrincipalKind()
    {
        Helpers.GetSecurityContext(Authenticated(("sub", "u1"))).PrincipalKind.Should().Be(RtPrincipalKind.Interactive);
        Helpers.GetSecurityContext(Authenticated(("client_id", "svc"))).PrincipalKind.Should().Be(RtPrincipalKind.Client);
        Helpers.GetSecurityContext(Authenticated(("sub", "u1"), ("act", "sa"))).PrincipalKind
            .Should().Be(RtPrincipalKind.Client);
        Helpers.GetSecurityContext(Authenticated(("scope", "x"))).PrincipalKind.Should().Be(RtPrincipalKind.Unknown);
        Helpers.GetSecurityContext((ClaimsPrincipal?)null).PrincipalKind.Should().Be(RtPrincipalKind.Unknown);
    }

    [Fact]
    public void GetSecurityContext_DelegatedToken_KeepsTheUserAsSubject()
    {
        // the subject (RtCreatedBy, data permissions) stays the user; only the kind says an automated actor writes
        var context = Helpers.GetSecurityContext(Authenticated(("sub", "u1"), ("act", "sa"), ("amr", "delegation")));

        context.SubjectId.Should().Be("u1");
        context.PrincipalKind.Should().Be(RtPrincipalKind.Client);
    }
}
