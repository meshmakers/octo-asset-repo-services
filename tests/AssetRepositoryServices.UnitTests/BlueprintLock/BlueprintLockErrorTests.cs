using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices;
using Meshmakers.Octo.Backend.AssetRepositoryServices.BlueprintLock;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace AssetRepositoryServices.UnitTests.BlueprintLock;

/// <summary>
///     AB#6385: the stable API contract of the engine's blueprint-lock refusal (message number 6384, AB#6384) in
///     GraphQL (<c>extensions.code = BLUEPRINT_LOCKED</c>) and REST (403 problem details, same code).
/// </summary>
public class BlueprintLockErrorTests
{
    private const string CkTypeId = "Accounting-1.0.0/CategorizationRule-1";
    private const string RtId = "65dc6d24cc529cdc46c84fcc";
    private const string OtherRtId = "65dc6d24cc529cdc46c84fcd";

    private static OperationMessage Locked(string rtId = RtId) => new(MessageLevel.Error, $"{CkTypeId}@{rtId}", 6384,
        $"Access denied: entity '{CkTypeId}@{rtId}' is locked by blueprint and cannot be changed.");

    private static OperationMessage ProtectedAttributes() => new(MessageLevel.Error, $"{CkTypeId}@{RtId}", 6384,
        $"Access denied: attribute(s) RtBlueprintLocked of '{CkTypeId}@{RtId}' are managed by blueprints (locked by blueprint) " +
        "and cannot be set or changed by users.");

    private static OperationResult Result(params OperationMessage[] messages)
    {
        var result = new OperationResult();
        foreach (var message in messages)
        {
            result.Messages.Add(message);
        }

        return result;
    }

    [Fact]
    public void Contract_IsStable()
    {
        BlueprintLockError.Code.Should().Be("BLUEPRINT_LOCKED");
        BlueprintLockError.MessageNumber.Should().Be(6384);
    }

    [Fact]
    public void GetOffenders_SplitsLocationAndClassifiesReason()
    {
        var offenders = BlueprintLockError.GetOffenders([Locked(), ProtectedAttributes()]);

        offenders.Should().HaveCount(2);
        offenders[0].Should().BeEquivalentTo(new { CkTypeId, RtId, Reason = BlueprintLockReason.EntityLocked });
        offenders[1].Reason.Should().Be(BlueprintLockReason.ProtectedAttributes);
    }

    [Fact]
    public void GetOffenders_IgnoresOtherMessages()
    {
        var other = new OperationMessage(MessageLevel.Error, $"{CkTypeId}@{RtId}", 4973, "Access denied: write");

        BlueprintLockError.GetOffenders([other]).Should().BeEmpty();
        BlueprintLockError.GetOffenders([new OperationMessage(MessageLevel.Warning, null, 6384, "w")]).Should().BeEmpty();
    }

    [Fact]
    public void GetOffenders_InsertWithoutRtId_HasNullRtId()
    {
        var message = new OperationMessage(MessageLevel.Error, $"{CkTypeId}@null", 6384, "Access denied: managed by blueprints");

        BlueprintLockError.GetOffenders([message]).Single().RtId.Should().BeNull();
    }

    [Fact]
    public void ValidateOperationResult_BlueprintLock_ThrowsBlueprintLockedException()
    {
        var act = () => ResolveConnectionContextExtensions.ValidateOperationResult(Result(Locked()));

        act.Should().Throw<BlueprintLockedException>().Which.Offenders.Should().ContainSingle();
    }

    [Fact]
    public void ValidateOperationResult_OtherError_StaysGeneric()
    {
        var result = Result(new OperationMessage(MessageLevel.Error, null, 4973, "Access denied"));

        var act = () => ResolveConnectionContextExtensions.ValidateOperationResult(result);

        act.Should().Throw<AssetRepositoryException>().And.Should().NotBeOfType<BlueprintLockedException>();
    }

    [Fact]
    public void GraphQl_MapsToStableCodeWithCkTypeIdRtIdAndReason()
    {
        var context = A.Fake<IResolveFieldContext>();
        var errors = new ExecutionErrors();
        A.CallTo(() => context.Errors).Returns(errors);
        TryThrow(() => ResolveConnectionContextExtensions.ValidateOperationResult(Result(Locked())), out var exception);

        context.HandleException(exception);

        var error = errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(Statics.GraphQlBlueprintLocked).And.Be("BLUEPRINT_LOCKED");
        error.Extensions!["messageNumber"].Should().Be(6384);
        error.Extensions["ckTypeId"].Should().Be(CkTypeId);
        error.Extensions["rtId"].Should().Be(RtId);
        error.Extensions["reason"].Should().Be("EntityLocked");
        error.Message.Should().Contain("locked by blueprint");
    }

    [Fact]
    public void GraphQl_Batch_ListsEveryRefusedEntity()
    {
        var context = A.Fake<IResolveFieldContext>();
        var errors = new ExecutionErrors();
        A.CallTo(() => context.Errors).Returns(errors);
        TryThrow(() => ResolveConnectionContextExtensions.ValidateOperationResult(
            Result(Locked(), Locked(OtherRtId))), out var exception);

        context.HandleException(exception);

        var error = errors.Should().ContainSingle().Subject;
        var items = (IEnumerable<Dictionary<string, object?>>)error.Extensions!["items"]!;
        items.Select(i => i["rtId"]).Should().Equal(RtId, OtherRtId);
    }

    [Fact]
    public void Rest_FilterAnswers403ProblemDetailsWithTheSameCode()
    {
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        TryThrow(() => ResolveConnectionContextExtensions.ValidateOperationResult(Result(Locked(), ProtectedAttributes())),
            out var exception);
        var context = new ExceptionContext(actionContext, []) { Exception = exception };

        new BlueprintLockedExceptionFilter().OnException(context);

        context.ExceptionHandled.Should().BeTrue();
        var result = context.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        result.ContentTypes.Should().Contain("application/problem+json");
        var problem = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(403);
        problem.Type.Should().Be(BlueprintLockedExceptionFilter.ProblemType);
        problem.Extensions["code"].Should().Be("BLUEPRINT_LOCKED");
        problem.Extensions["messageNumber"].Should().Be(6384);
        problem.Extensions["ckTypeId"].Should().Be(CkTypeId);
        problem.Extensions["rtId"].Should().Be(RtId);

        // The problem must serialise (no unsupported members) and keep the extension names on the wire.
        var json = JsonSerializer.Serialize(problem);
        json.Should().Contain("\"code\":\"BLUEPRINT_LOCKED\"").And.Contain("\"items\"");
    }

    [Fact]
    public void Rest_FilterIgnoresOtherExceptions()
    {
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        var context = new ExceptionContext(actionContext, []) { Exception = new InvalidOperationException("x") };

        new BlueprintLockedExceptionFilter().OnException(context);

        context.ExceptionHandled.Should().BeFalse();
        context.Result.Should().BeNull();
    }

    private static void TryThrow(Action action, out Exception exception)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            exception = e;
            return;
        }

        throw new InvalidOperationException("Expected an exception.");
    }
}
