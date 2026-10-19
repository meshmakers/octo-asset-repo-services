using Meshmakers.Octo.Services.Methods;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;

/// <summary>
///     Tripwire of the in-process CK method transport for the request session (AB#7023, OD-8 part 2). The transport runs
///     every in-process handler detached from the ambient execution context of the caller, so the
///     <see cref="OctoSessionAccessor" /> <c>AsyncLocal</c> of the GraphQL request is empty inside the handler. The
///     transport asks this probe at handler entry, in the handler's own flow and new DI scope, before the handler is
///     created; when <see cref="IOctoSessionAccessor.HasSession" /> is <c>true</c> the call fails with
///     <c>METHOD_HANDLER_FAILED</c> and the handler body does not run. <c>true</c> can only mean a regression (somebody
///     reintroduced an ambient value or bypassed the detach). The probe answers yes/no and never exposes the session.
///     A missing accessor throws, which the transport also treats as a failed call (fail closed).
/// </summary>
internal sealed class OctoSessionAmbientStateProbe : ICkInProcessAmbientStateProbe
{
    /// <inheritdoc />
    public bool IsRequestStateVisible(IServiceProvider handlerServices)
    {
        ArgumentNullException.ThrowIfNull(handlerServices);
        return handlerServices.GetService(typeof(IOctoSessionAccessor)) is IOctoSessionAccessor accessor
            ? accessor.HasSession
            : throw new InvalidOperationException("IOctoSessionAccessor is not registered; the request session cannot be ruled out.");
    }
}
