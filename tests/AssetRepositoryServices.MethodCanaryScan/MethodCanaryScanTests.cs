using System.Text.Json.Nodes;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.MethodCanaryScan;

/// <summary>
///     AB#7118: canary scan of the method path on the LOCAL stack. A sensitive parameter (the canaries of AB#6914 / AB#7015) and
///     the caller's access token must reach none of: GraphQL answers, the service log files, the tenant's audit / event log,
///     exported traces, metrics and logs (OTLP), and, for the asset-repo method, the log and spans of the handling service.
///     Runs only with <c>OCTO_METHOD_CANARY_SCAN=1</c>; set-up and the command are in README.md.
/// </summary>
public sealed class MethodCanaryScanTests
{
    private const string RotateMutation = """
        mutation($rtId: OctoObjectId!, $input: MethodCanaryScanCanaryAccountRotateMethodInput!) {
          runtime { methodCanaryScanCanaryAccounts { rotate(rtId: $rtId, input: $input) { invocationId } } } }
        """;

    private const string GuardedMutation = """
        mutation($rtId: OctoObjectId!, $input: MethodCanaryScanCanaryAccountGuardedMethodInput!) {
          runtime { methodCanaryScanCanaryAccounts { guarded(rtId: $rtId, input: $input) { invocationId } } } }
        """;

    private const string CreateUserMutation = """
        mutation($input: SystemIdentityUserCreateUserMethodInput!) {
          runtime { systemIdentityUsers { createUser(input: $input) { invocationId result } } } }
        """;

    private static readonly (string Label, string Value)[] CanarySet =
        [("plain", Canaries.Plain), ("escaping", Canaries.Escaping)];

    /// <summary>
    ///     Asset repository: the typed method mutation of AB#7026 calls <c>MethodCanaryScan/CanaryAccount.Rotate-1</c>, handled by the
    ///     scan's handler host over the bus: success, handler failure (plain and JSON-escaped exception text), timeout,
    ///     validation errors (handler, GraphQL input), a declared error and refused calls (METHOD_FORBIDDEN, forged token).
    /// </summary>
    [Fact]
    public async Task AssetRepositoryMethod_CanaryReachesNoSink()
    {
        Assert.SkipUnless(ScanSettings.IsEnabled, $"set {ScanSettings.GateVariable}=1 to scan the local stack (README.md)");
        var ct = TestContext.Current.CancellationToken;
        var settings = ScanSettings.FromEnvironment();
        using var run = new ScanRun("asset repository method (MethodCanaryScan/CanaryAccount.Rotate-1)", settings);
        var adminToken = await run.Client.GetTokenAsync(settings.ClientId, settings.ClientSecret, ct);
        run.AddToken("access token (admin)", adminToken);
        run.AddSecret("scan client secret", settings.ClientSecret);
        var sink = new CaptureSink(Path.Combine(settings.ResultDirectory.FullName, "method-canary-scan-handler-host.log"));
        run.HandlerHost = sink;
        using var listener = sink.CreateActivityListener();
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        var (noRoleId, noRoleToken) = await CreateCallerWithoutRolesAsync(run, adminToken, ct);
        try
        {
            await using (await ScanHandlerHost.StartAsync(settings, sink, ct))
            {
                // the dispatcher of the asset repository learns the handler from its announcement
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                var target = await CreateTargetAsync(run, adminToken, ct);
                foreach (var (label, canary) in CanarySet)
                {
                    foreach (var (mode, expected) in new[]
                             {
                                 ("ok", "SUCCESS"), ("fail", "METHOD_HANDLER_FAILED"), ("failjson", "METHOD_HANDLER_FAILED"),
                                 ("hang", "METHOD_TIMEOUT"), ("invalid", "METHOD_VALIDATION_FAILED"), ("declared", "CANARY_REJECTED")
                             })
                    {
                        run.Record($"(rotate) handler mode {mode}", label, expected, await run.Client.GraphQlAsync(adminToken,
                            RotateMutation, Variables(target, new JsonObject { ["secret"] = canary, ["mode"] = mode }), ct));
                    }

                    // GraphQL input validation: wrong shape and an unknown member, both carrying the canary
                    run.Record("(rotate) GraphQL input: secret is an object", label, "*", await run.Client.GraphQlAsync(adminToken,
                        RotateMutation, Variables(target, new JsonObject { ["secret"] = new JsonObject { ["inner"] = canary } }), ct));
                    run.Record("(rotate) GraphQL input: unknown member", label, "*", await run.Client.GraphQlAsync(adminToken,
                        RotateMutation, Variables(target, new JsonObject { ["secret"] = "x", ["password"] = canary }), ct));
                    // refused: a role nobody holds, a caller without roles, a forged token
                    run.Record("(guarded) declared role held by nobody", label, "METHOD_FORBIDDEN", await run.Client.GraphQlAsync(
                        adminToken, GuardedMutation, Variables(target, new JsonObject { ["secret"] = canary }), ct));
                    run.Record("(rotate) caller without roles", label, "METHOD_FORBIDDEN", await run.Client.GraphQlAsync(noRoleToken,
                        RotateMutation, Variables(target, new JsonObject { ["secret"] = canary, ["mode"] = "ok" }), ct));
                    run.Record("(rotate) forged bearer token", label, "HTTP 401", await run.Client.GraphQlAsync(Canaries.ForgedToken,
                        RotateMutation, Variables(target, new JsonObject { ["secret"] = canary }), ct));
                }
            }
        }
        finally
        {
            await run.Client.DeleteClientAsync(adminToken, noRoleId, CancellationToken.None);
        }

        await AssertCleanAsync(run, "asset-repo", adminToken, ct);
    }

    /// <summary>
    ///     Identity service: the first method it hosts with a sensitive parameter, <c>System.Identity/User.CreateUser-1</c>
    ///     (password), through the same typed mutation: success, declared errors (user exists, password policy), GraphQL
    ///     input validation and refused calls.
    /// </summary>
    [Fact]
    public async Task IdentityMethod_CanaryReachesNoSink()
    {
        Assert.SkipUnless(ScanSettings.IsEnabled, $"set {ScanSettings.GateVariable}=1 to scan the local stack (README.md)");
        var ct = TestContext.Current.CancellationToken;
        var settings = ScanSettings.FromEnvironment();
        using var run = new ScanRun("identity method (System.Identity/User.CreateUser-1)", settings);
        var adminToken = await run.Client.GetTokenAsync(settings.ClientId, settings.ClientSecret, ct);
        run.AddToken("access token (admin)", adminToken);
        run.AddSecret("scan client secret", settings.ClientSecret);
        var (noRoleId, noRoleToken) = await CreateCallerWithoutRolesAsync(run, adminToken, ct);
        try
        {
            foreach (var (label, canary) in CanarySet)
            {
                var userName = "canary-scan-" + Guid.NewGuid().ToString("N")[..10];
                // a password the default policy accepts: the canary plus lower-case letters and a digit
                var password = canary + "-ok7x";
                run.Record("(createUser) new user", label, "SUCCESS", await run.Client.GraphQlAsync(adminToken, CreateUserMutation,
                    UserInput(userName, password), ct));
                run.Record("(createUser) user exists (declared error)", label, "USER_ALREADY_EXISTS", await run.Client.GraphQlAsync(
                    adminToken, CreateUserMutation, UserInput(userName, password), ct));
                run.Record("(createUser) password too weak (declared error)", label, "PASSWORD_POLICY_VIOLATION",
                    await run.Client.GraphQlAsync(adminToken, CreateUserMutation, UserInput(userName + "-weak", canary.ToUpperInvariant()), ct));
                run.Record("(createUser) GraphQL input: password is an object", label, "*", await run.Client.GraphQlAsync(adminToken,
                    CreateUserMutation, new JsonObject
                    {
                        ["input"] = new JsonObject { ["userName"] = userName + "-obj", ["password"] = new JsonObject { ["inner"] = canary } }
                    }, ct));
                run.Record("(createUser) caller without roles", label, "METHOD_FORBIDDEN", await run.Client.GraphQlAsync(noRoleToken,
                    CreateUserMutation, UserInput(userName + "-norole", password), ct));
                run.Record("(createUser) forged bearer token", label, "HTTP 401", await run.Client.GraphQlAsync(Canaries.ForgedToken,
                    CreateUserMutation, UserInput(userName + "-forged", password), ct));
            }
        }
        finally
        {
            await run.Client.DeleteClientAsync(adminToken, noRoleId, CancellationToken.None);
        }

        run.NotCovered("METHOD_TIMEOUT for User.CreateUser-1: the identity handler cannot be made to exceed its timeout without a " +
                       "fault injection in the service; the timeout path of the gateway and dispatcher is covered by the asset-repo scan " +
                       "(mode hang).");
        await AssertCleanAsync(run, "identity", adminToken, ct);
    }

    private static JsonObject Variables(string rtId, JsonObject input) => new() { ["rtId"] = rtId, ["input"] = input };

    private static JsonObject UserInput(string userName, string password) => new()
    {
        ["input"] = new JsonObject { ["userName"] = userName, ["email"] = userName + "@canary.invalid", ["password"] = password }
    };

    private static async Task<string> CreateTargetAsync(ScanRun run, string adminToken, CancellationToken ct)
    {
        var answer = await run.Client.GraphQlAsync(adminToken, """
            mutation { runtime { methodCanaryScanCanaryAccounts { create(entities: [{ name: "canary-scan-target" }]) { rtId } } } }
            """, null, ct);
        return answer.Json?["data"]?["runtime"]?["methodCanaryScanCanaryAccounts"]?["create"]?[0]?["rtId"]?.GetValue<string>()
               ?? throw new InvalidOperationException("Creating the scan target failed (is the scan CK model imported? README.md): " +
                                                      answer.Outcome);
    }

    /// <summary>A client of the tenant without any role; its secret and token are searched like the canaries.</summary>
    private static async Task<(string ClientId, string Token)> CreateCallerWithoutRolesAsync(ScanRun run, string adminToken,
        CancellationToken ct)
    {
        var clientId = "canary-scan-norole-" + Guid.NewGuid().ToString("N")[..8];
        var secret = ScanRun.NewClientSecret();
        run.AddSecret("secret of the client without roles", secret);
        await run.Client.CreateClientWithoutRolesAsync(adminToken, clientId, secret, ct);
        var token = await run.Client.GetTokenAsync(clientId, secret, ct);
        run.AddToken("access token (no roles)", token);
        return (clientId, token);
    }

    private static async Task AssertCleanAsync(ScanRun run, string slug, string adminToken, CancellationToken ct)
    {
        var (hits, report) = await run.FinishAsync(slug, adminToken, ct);
        TestContext.Current.SendDiagnosticMessage($"Canary scan report: {report}");
        var unexpected = run.Cases.Where(c => !c.AsExpected).ToList();
        Assert.True(unexpected.Count == 0,
            "cases did not reach the path they scan (see " + report + "): " +
            string.Join("; ", unexpected.Select(c => $"{c.Name} [{c.Canary}] expected {c.Expected}, got {c.Actual}")));
        Assert.True(hits.Count == 0, $"{hits.Count} canary hit(s), see {report}: " +
                                     string.Join(" | ", hits.Select(h => $"{h.Needle} in {h.Sink} {h.Source}")));
        Assert.True(run.TelemetryNotCaptured == null || Environment.GetEnvironmentVariable("OCTO_CANARY_ALLOW_NO_OTLP") == "1",
            "exported telemetry was not scanned: " + run.TelemetryNotCaptured + " (set OCTO_CANARY_ALLOW_NO_OTLP=1 to accept, see " +
            report + ")");
    }
}
