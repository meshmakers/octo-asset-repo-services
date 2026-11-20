using System.Reflection;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AssetRepositoryServices.UnitTests.TenantApi;

/// <summary>
///     The read-only blueprint update previews (<c>GET blueprints/updates</c>,
///     <c>POST blueprints/updates/preview</c>) are open to read-only callers and keep the runtime query time limit; a
///     preview above the limit ends with <c>504</c> / <c>ASSET1008</c> like any query, never 400 or 500. The update run
///     (<c>POST blueprints/updates/apply</c>) is a batch path and runs without the limit.
/// </summary>
public class BlueprintsControllerTimeLimitTests
{
    private const string TenantId = "meshtest";
    private const string BlueprintName = "EnergyCommunity.EdaIntegration";

    private readonly IBlueprintService _blueprintService = A.Fake<IBlueprintService>();
    private readonly BlueprintsController _controller;

    public BlueprintsControllerTimeLimitTests()
    {
        _controller = new BlueprintsController(
            A.Fake<ITenantBlueprintHistory>(),
            _blueprintService,
            A.Fake<ITenantBlueprintInstallations>(),
            A.Fake<IAuthorizationService>(),
            NullLogger<BlueprintsController>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BlueprintUpdateRequestDto UpdateRequest() => new() { TargetVersion = $"{BlueprintName}-2.2.1" };

    private static RuntimeQueryTimeoutException Timeout() =>
        new(TenantId, "System.Communication/Pipeline-1", TimeSpan.FromSeconds(120),
            TimeSpan.FromMilliseconds(120_015), null);

    [Fact]
    public async Task GetAvailableUpdates_KeepsTheTimeLimit()
    {
        ScopeSeen seen = ScopeSeen.NotCalled;
        A.CallTo(() => _blueprintService.GetUpdateInfoAsync(TenantId, A<string?>._, A<CancellationToken>._))
            .Invokes(() => seen = TimeLimitScope.Read())
            .Returns((BlueprintUpdateInfo?)null);

        var result = await _controller.GetAvailableUpdates(BlueprintName, Ct);

        result.Should().BeOfType<OkObjectResult>();
        seen.Should().Be(ScopeSeen.NoScope, "a read-only preview runs with the configured limit");
    }

    [Fact]
    public async Task PreviewUpdate_KeepsTheTimeLimit()
    {
        ScopeSeen seen = ScopeSeen.NotCalled;
        A.CallTo(() => _blueprintService.PreviewUpdateAsync(
                TenantId, A<BlueprintId>._, A<BlueprintUpdateMode>._, A<CancellationToken>._))
            .Invokes(() => seen = TimeLimitScope.Read())
            .Returns(new BlueprintUpdatePreview());

        var result = await _controller.PreviewUpdate(UpdateRequest(), Ct);

        result.Should().BeOfType<OkObjectResult>();
        seen.Should().Be(ScopeSeen.NoScope, "a read-only preview runs with the configured limit");
    }

    [Fact]
    public async Task ApplyUpdate_RunsWithoutTheTimeLimit()
    {
        ScopeSeen seen = ScopeSeen.NotCalled;
        A.CallTo(() => _blueprintService.ApplyUpdateAsync(
                TenantId, A<BlueprintId>._, A<BlueprintUpdateMode>._, A<BlueprintUpdateOptions?>._,
                A<CancellationToken>._))
            .Invokes(() => seen = TimeLimitScope.Read())
            .Returns(new BlueprintUpdateResult { Success = true });

        await _controller.ApplyUpdate(UpdateRequest(), Ct);

        seen.Should().Be(ScopeSeen.Disabled, "the update run is a batch path");
    }

    /// <summary>
    ///     The scope the batch paths open ends when it is disposed. Checked in one execution context: a scope set inside
    ///     an awaited action never flows back to the caller, so reading it after the action would prove nothing.
    /// </summary>
    [Fact]
    public void DisabledScope_EndsWhenDisposed()
    {
        TimeLimitScope.Read().Should().Be(ScopeSeen.NoScope);

        var noQueryTimeLimit = RuntimeQueryTimeLimit.Disable();
        TimeLimitScope.Read().Should().Be(ScopeSeen.Disabled);

        noQueryTimeLimit.Dispose();
        TimeLimitScope.Read().Should().Be(ScopeSeen.NoScope, "the limit applies again after the batch path");
    }

    public static TheoryData<bool> Wrapped => new() { false, true };

    [Theory]
    [MemberData(nameof(Wrapped))]
    public async Task GetAvailableUpdates_TimeOut_AnswersAsGatewayTimeout(bool wrapped)
    {
        A.CallTo(() => _blueprintService.GetUpdateInfoAsync(TenantId, A<string?>._, A<CancellationToken>._))
            .Throws(Failure(wrapped));

        var thrown = await FluentActions.Awaiting(() => _controller.GetAvailableUpdates(BlueprintName, Ct))
            .Should().ThrowAsync<Exception>();

        AssertAnsweredAsGatewayTimeout(thrown.Which);
    }

    [Theory]
    [MemberData(nameof(Wrapped))]
    public async Task PreviewUpdate_TimeOut_AnswersAsGatewayTimeout(bool wrapped)
    {
        A.CallTo(() => _blueprintService.PreviewUpdateAsync(
                TenantId, A<BlueprintId>._, A<BlueprintUpdateMode>._, A<CancellationToken>._))
            .Throws(Failure(wrapped));

        var thrown = await FluentActions.Awaiting(() => _controller.PreviewUpdate(UpdateRequest(), Ct))
            .Should().ThrowAsync<Exception>();

        AssertAnsweredAsGatewayTimeout(thrown.Which);
    }

    /// <summary>
    ///     The time-out as the engine throws it, or wrapped in a <see cref="PersistenceException" />, which the actions
    ///     otherwise answer with 400.
    /// </summary>
    private static Exception Failure(bool wrapped) =>
        wrapped ? new WrappingPersistenceException(Timeout()) : Timeout();

    /// <summary>
    ///     The action passes the time-out on (no 400 or 500 of its own), and the registered MVC filter answers it with
    ///     <c>504</c> and the code <c>ASSET1008</c>.
    /// </summary>
    private static void AssertAnsweredAsGatewayTimeout(Exception thrown)
    {
        var context = new ExceptionContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), [])
        {
            Exception = thrown
        };

        new RuntimeQueryTimeoutExceptionFilter(LoggerFactory.Create(_ => { })).OnException(context);

        context.ExceptionHandled.Should().BeTrue();
        var result = context.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status504GatewayTimeout);
        result.Value.Should().BeOfType<ProblemDetails>().Which.Extensions["code"].Should().Be("ASSET1008");
    }

    private enum ScopeSeen
    {
        NotCalled,
        NoScope,
        Disabled,
        Overridden
    }

    /// <summary>
    ///     Reads the innermost <see cref="RuntimeQueryTimeLimit" /> scope of the current execution context. The engine
    ///     keeps it internal; reflection keeps the test independent of an engine test hook and fails loudly if the
    ///     member is renamed.
    /// </summary>
    private static class TimeLimitScope
    {
        private static readonly PropertyInfo Current =
            typeof(RuntimeQueryTimeLimit).GetProperty("Current", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("RuntimeQueryTimeLimit.Current not found");

        public static ScopeSeen Read()
        {
            var scope = Current.GetValue(null);
            if (scope == null)
            {
                return ScopeSeen.NoScope;
            }

            var maxTime = scope.GetType().GetProperty("MaxTime")
                          ?? throw new InvalidOperationException("RuntimeQueryTimeLimit.Limit.MaxTime not found");
            return maxTime.GetValue(scope) == null ? ScopeSeen.Disabled : ScopeSeen.Overridden;
        }
    }

    private sealed class WrappingPersistenceException(Exception inner)
        : PersistenceException("Preview failed", inner);
}
