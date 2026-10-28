using Meshmakers.Octo.Services.Methods;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;

/// <summary>
///     The CK methods that the asset repository owns itself (<c>handler: service</c> on a model the gateway hosts, for
///     example <c>Tenant.*</c> and <c>Archive.*</c>), registered as in-process handlers (AB#6893, F3.7). There is no
///     production method yet: the list is empty and each owning story adds one line here, next to its handler:
///     <code>
///     methods.Handle&lt;ArchiveActivateHandler&gt;(SystemCkIds.ArchiveActivateMethodId);
///     </code>
///     Use the version-less qualified method id. The handler class is the same typed handler that works under the bus
///     transport. Rules that hold for every handler registered here: it runs in a new DI scope outside the request session
///     (<c>IOctoSessionAccessor.HasSession</c> is <c>false</c>, AB#7023), it must resolve its tenant from the request, and
///     it can only be reached through the invocation pipeline, which enforces the declared <c>authorization</c> of the
///     method (no principal, no call).
/// </summary>
internal static class GatewayOwnedMethodHandlers
{
    /// <summary>Adds the handlers of the methods the asset repository owns.</summary>
    internal static void Register(ICkMethodHandlerRegistrationBuilder methods)
    {
        ArgumentNullException.ThrowIfNull(methods);
        // No production method is owned by the gateway yet (Tenant.*, Archive.* follow with their own stories).
    }
}
