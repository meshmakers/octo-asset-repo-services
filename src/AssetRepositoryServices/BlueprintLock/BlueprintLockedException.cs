namespace Meshmakers.Octo.Backend.AssetRepositoryServices.BlueprintLock;

/// <summary>
///     A change set was refused because it touches blueprint-locked entities or blueprint bookkeeping attributes of a
///     type that opted in via <c>DataPolicy.ProtectBlueprintLocked</c> (AB#6384). Mapped to the stable error
///     <see cref="BlueprintLockError.Code" /> in GraphQL and REST (AB#6385).
/// </summary>
internal sealed class BlueprintLockedException : AssetRepositoryException
{
    public BlueprintLockedException(IReadOnlyList<BlueprintLockOffender> offenders)
        : base(BuildMessage(offenders))
    {
        Offenders = offenders;
        foreach (var offender in offenders)
        {
            Details.Add(new DetailMessage { Message = $"{BlueprintLockError.MessageNumber}: {offender.Message}" });
        }
    }

    /// <summary>The refused entities; never empty.</summary>
    public IReadOnlyList<BlueprintLockOffender> Offenders { get; }

    /// <summary>The first offender; carries the top-level <c>ckTypeId</c>, <c>rtId</c> and <c>reason</c> of the error.</summary>
    public BlueprintLockOffender First => Offenders[0];

    private static string BuildMessage(IReadOnlyList<BlueprintLockOffender> offenders)
    {
        return offenders.Count == 1
            ? offenders[0].Message
            : $"Access denied: {offenders.Count} entities are locked by blueprint and cannot be changed by users. " +
              "Nothing was written.";
    }
}
