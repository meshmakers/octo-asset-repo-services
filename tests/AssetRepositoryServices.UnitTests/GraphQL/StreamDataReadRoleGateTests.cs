using System.Security.Claims;
using FakeItEasy;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     AB#8559: stream-data reads require one of the tenant roles StreamDataReader, StreamDataWriter or StreamDataAdmin,
///     staged Off / Warn / Enforce. System contexts always read.
/// </summary>
public class StreamDataReadRoleGateTests
{
    private const string TenantId = "meshtest";

    private readonly StreamDataReadAuthorizationOptions _options = new();
    private readonly CapturingLogger _logger = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
    private readonly StreamDataReadRoleGate _gate;

    public StreamDataReadRoleGateTests()
    {
        var monitor = A.Fake<IOptionsMonitor<StreamDataReadAuthorizationOptions>>();
        A.CallTo(() => monitor.CurrentValue).ReturnsLazily(() => _options);
        _gate = new StreamDataReadRoleGate(monitor, _logger, _time);
    }

    public static TheoryData<StreamDataReadRoleEnforcementMode, string> ModesAndRoles()
    {
        var data = new TheoryData<StreamDataReadRoleEnforcementMode, string>();
        foreach (var mode in Enum.GetValues<StreamDataReadRoleEnforcementMode>())
        {
            data.Add(mode, CommonConstants.StreamDataReaderRole);
            data.Add(mode, CommonConstants.StreamDataWriterRole);
            data.Add(mode, CommonConstants.StreamDataAdminRole);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ModesAndRoles))]
    public void StreamDataRole_ReadsInEveryMode(StreamDataReadRoleEnforcementMode mode, string role)
    {
        _options.RoleEnforcement = mode;

        _gate.Check(Principal(role), Ctx(Principal(role)), TenantId, "simple")
            .Should().Be(StreamDataReadRoleDecision.Allowed);
        _logger.Entries.Should().BeEmpty();
    }

    [Theory]
    [InlineData("role")]
    [InlineData(ClaimTypes.Role)]
    public void StreamDataRole_IsFoundUnderBothClaimSpellings(string claimType)
    {
        _options.RoleEnforcement = StreamDataReadRoleEnforcementMode.Enforce;
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "u1"), new Claim(claimType, CommonConstants.StreamDataReaderRole)], "Bearer"));

        _gate.Check(user, Ctx(user), TenantId, "simple").Should().Be(StreamDataReadRoleDecision.Allowed);
    }

    [Theory]
    [InlineData(StreamDataReadRoleEnforcementMode.Off)]
    [InlineData(StreamDataReadRoleEnforcementMode.Warn)]
    [InlineData(StreamDataReadRoleEnforcementMode.Enforce)]
    public void SystemContext_ReadsInEveryMode(StreamDataReadRoleEnforcementMode mode)
    {
        _options.RoleEnforcement = mode;

        _gate.Check(Principal(), RtSecurityContext.System, TenantId, "simple")
            .Should().Be(StreamDataReadRoleDecision.Allowed);
        _gate.Check(null, RtSecurityContext.ForUser(null, null), TenantId, "simple")
            .Should().Be(StreamDataReadRoleDecision.Allowed, "an in-process execution without a principal is internal");
        _logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void NoRole_Off_ReadsWithoutLog()
    {
        _options.RoleEnforcement = StreamDataReadRoleEnforcementMode.Off;
        var user = Principal("MeterReporter");

        _gate.Check(user, Ctx(user), TenantId, "simple").Should().Be(StreamDataReadRoleDecision.Allowed);
        _logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void NoRole_Warn_ReadsAndLogsIdsOnly()
    {
        _options.RoleEnforcement = StreamDataReadRoleEnforcementMode.Warn;
        var user = Principal("MeterReporter", clientId: "pipeline-sa", subject: null);

        _gate.Check(user, Ctx(user), TenantId, "downsampling").Should().Be(StreamDataReadRoleDecision.AllowedByWarn);

        var entry = _logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("query downsampling").And.Contain($"tenant {TenantId}")
            .And.Contain("client pipeline-sa").And.Contain("StreamDataReadRoleEnforcement=Enforce (AB#8559)")
            .And.NotContain("MeterReporter", "the log names ids and the required roles, not the token's content");
    }

    [Fact]
    public void NoRole_Warn_LogsOncePerCallerTenantAndKindWithinTheWindow()
    {
        _options.RoleEnforcement = StreamDataReadRoleEnforcementMode.Warn;
        _options.WarnLogInterval = TimeSpan.FromMinutes(10);
        var alice = Principal(subject: "alice");
        var bob = Principal(subject: "bob");

        _gate.Check(alice, Ctx(alice), TenantId, "simple");
        _gate.Check(alice, Ctx(alice), TenantId, "simple");
        _logger.Entries.Should().HaveCount(1, "the same caller, tenant and kind is logged once per window");

        _gate.Check(alice, Ctx(alice), TenantId, "aggregation");
        _gate.Check(alice, Ctx(alice), "othertenant", "simple");
        _gate.Check(bob, Ctx(bob), TenantId, "simple");
        _logger.Entries.Should().HaveCount(4, "another kind, tenant or caller is logged on its own");

        _time.Advance(TimeSpan.FromMinutes(11));
        _gate.Check(alice, Ctx(alice), TenantId, "simple");
        _logger.Entries.Should().HaveCount(5, "after the window the caller is logged again");
    }

    [Fact]
    public void NoRole_Enforce_IsDeniedWithStableException()
    {
        _options.RoleEnforcement = StreamDataReadRoleEnforcementMode.Enforce;
        var user = Principal("MeterReporter", clientId: "ec-reporter");

        _gate.Check(user, Ctx(user), TenantId, "simple").Should().Be(StreamDataReadRoleDecision.Denied);
        _logger.Entries.Should().ContainSingle(e => e.Message.Contains("Stream-data read denied"));

        var act = () => _gate.EnsureAllowed(user, Ctx(user), TenantId, "simple");
        act.Should().Throw<StreamDataReadRoleRequiredException>()
            .Which.Message.Should().Contain(CommonConstants.StreamDataReaderRole)
            .And.Contain(CommonConstants.StreamDataWriterRole)
            .And.Contain(CommonConstants.StreamDataAdminRole);
    }

    [Fact]
    public void DeniedRead_MapsToTheStableGraphQlCode()
    {
        var errors = new ExecutionErrors();
        var ctx = A.Fake<IResolveFieldContext>();
        A.CallTo(() => ctx.Errors).Returns(errors);

        ctx.HandleException(new StreamDataReadRoleRequiredException());

        errors.Should().ContainSingle().Which.Code.Should().Be("StreamDataReadRoleRequired");
        AssetRepositoryServiceConstants.StreamDataReadRoleRequiredCode.Should().Be("StreamDataReadRoleRequired");
    }

    [Fact]
    public void Defaults_AreWarnAndOneHour()
    {
        var options = new StreamDataReadAuthorizationOptions();

        options.RoleEnforcement.Should().Be(StreamDataReadRoleEnforcementMode.Warn);
        options.WarnLogInterval.Should().Be(TimeSpan.FromHours(1));
        StreamDataReadAuthorizationOptions.SectionName.Should().Be("StreamDataReadAuthorization");
    }

    private static ClaimsPrincipal Principal(string? role = null, string? clientId = null, string? subject = "user-1")
    {
        var claims = new List<Claim>();
        if (subject != null)
        {
            claims.Add(new Claim("sub", subject));
        }

        if (clientId != null)
        {
            claims.Add(new Claim("client_id", clientId));
        }

        if (role != null)
        {
            claims.Add(new Claim("role", role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static RtSecurityContext Ctx(ClaimsPrincipal user) =>
        Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Helpers.GetSecurityContext(user);

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger<StreamDataReadRoleGate>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
