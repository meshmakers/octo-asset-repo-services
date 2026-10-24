using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;

/// <summary>Outcome of the data-permission check of a method call (AB#7037).</summary>
public enum CkMethodTargetAccessOutcome
{
    /// <summary>The caller may perform the data action on the target (or on the type, for a static method).</summary>
    Allowed,

    /// <summary>
    ///     The target does not exist for the caller: it is missing, or a data permission hides it. Both answer
    ///     <c>METHOD_TARGET_NOT_FOUND</c>, so the caller cannot tell them apart.
    /// </summary>
    NotVisible,

    /// <summary>The target is visible but the caller may not perform the data action: <c>METHOD_FORBIDDEN</c>.</summary>
    NotPermitted
}

/// <summary>Result of a <see cref="CkMethodTargetAccessCheck" />. Carries no entity data.</summary>
/// <param name="Outcome">The verdict.</param>
/// <param name="AuditOnlyViolation">An AuditOnly policy would have refused the caller; the call passes and the gateway logs the signal.</param>
public readonly record struct CkMethodTargetAccess(CkMethodTargetAccessOutcome Outcome, bool AuditOnlyViolation = false)
{
    /// <summary>The caller may proceed.</summary>
    public static CkMethodTargetAccess Allowed { get; } = new(CkMethodTargetAccessOutcome.Allowed);

    /// <summary>The target is missing or hidden.</summary>
    public static CkMethodTargetAccess NotVisible { get; } = new(CkMethodTargetAccessOutcome.NotVisible);

    /// <summary>The target is visible but the action is refused.</summary>
    public static CkMethodTargetAccess NotPermitted { get; } = new(CkMethodTargetAccessOutcome.NotPermitted);
}

/// <summary>
///     Loads the target of a method call with the rights of the caller and judges the data action the method needs on it
///     (<see cref="CkMethodDataAccessDto.Write" /> unless the method declares <c>Read</c>); for a static method it judges the
///     declaring type only. The gateway calls it once, after declared authorization and never for a refused call. A thrown
///     exception makes the call fail, never pass.
/// </summary>
public delegate Task<CkMethodTargetAccess> CkMethodTargetAccessCheck(CkMethodDataAccessDto dataAccess,
    CancellationToken cancellationToken);

/// <summary>The data-permission refusal of a method call (AB#7037).</summary>
internal static class CkMethodDataPermissionErrors
{
    /// <summary>The one reason of a refusal by a data permission. Fixed text: it never names the target, the policy or the role.</summary>
    public const string ReasonDataPermission = "data-permission";

    /// <summary><c>METHOD_FORBIDDEN</c> with the method id and the fixed data-permission reason.</summary>
    public static CkMethodError Forbidden(string methodId) =>
        CkMethodError.Create(CkMethodErrorCodes.MethodForbidden,
            (CkMethodErrorParameters.MethodId, methodId),
            (CkMethodErrorParameters.Reason, ReasonDataPermission));
}
