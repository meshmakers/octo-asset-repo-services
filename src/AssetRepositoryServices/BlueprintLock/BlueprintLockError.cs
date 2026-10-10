using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.BlueprintLock;

/// <summary>
///     Why a change was refused by the blueprint-lock protection (AB#6384, API contract AB#6385).
/// </summary>
internal enum BlueprintLockReason
{
    /// <summary>The entity is locked by a blueprint (<c>RtBlueprintLocked = true</c>) and cannot be changed or deleted.</summary>
    EntityLocked,

    /// <summary>The blueprint bookkeeping attributes cannot be set or changed by users.</summary>
    ProtectedAttributes
}

/// <summary>
///     One refused entity of a blueprint-lock rejection.
/// </summary>
/// <param name="CkTypeId">The CK type id as reported by the engine (<c>Model-1.0.0/Type-1</c> form).</param>
/// <param name="RtId">The runtime id of the entity; null for an insert that has no id yet.</param>
/// <param name="Reason">Why the change was refused.</param>
/// <param name="Message">The engine's message text.</param>
internal sealed record BlueprintLockOffender(string CkTypeId, string? RtId, BlueprintLockReason Reason, string Message);

/// <summary>
///     The stable API contract of the blueprint-lock refusal (AB#6385). The engine write guard (AB#6384) reports it as an
///     error <see cref="OperationMessage" /> with number <see cref="MessageNumber" /> whose location is
///     <c>{ckTypeId}@{rtId}</c>; this class turns those messages into the structured error that GraphQL
///     (<c>extensions.code = BLUEPRINT_LOCKED</c>) and REST (problem details, HTTP 403, <c>code = BLUEPRINT_LOCKED</c>) return.
/// </summary>
internal static class BlueprintLockError
{
    /// <summary>The stable, machine-readable error code of GraphQL <c>extensions.code</c> and the REST problem <c>code</c>.</summary>
    public const string Code = "BLUEPRINT_LOCKED";

    /// <summary>The engine message number of the refusal (<c>RtBlueprintLockProtectionNames.ForbiddenMessageNumber</c>).</summary>
    public const int MessageNumber = RtBlueprintLockProtectionNames.ForbiddenMessageNumber;

    /// <summary>The marker of the engine message that refuses blueprint bookkeeping attributes (not a locked entity).</summary>
    private const string ProtectedAttributesMarker = "managed by blueprints";

    /// <summary>
    ///     Extracts the blueprint-lock offenders from the error messages of an operation result.
    /// </summary>
    public static IReadOnlyList<BlueprintLockOffender> GetOffenders(IEnumerable<OperationMessage> messages)
    {
        return messages
            .Where(m => m.MessageNumber == MessageNumber &&
                        m.MessageLevel is MessageLevel.Error or MessageLevel.FatalError)
            .Select(ToOffender)
            .ToList();
    }

    /// <summary>
    ///     Creates the exception for an operation result that contains blueprint-lock errors.
    /// </summary>
    public static bool TryCreateException(OperationResult operationResult,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BlueprintLockedException? exception)
    {
        var offenders = GetOffenders(operationResult.Messages);
        if (offenders.Count == 0)
        {
            exception = null;
            return false;
        }

        exception = new BlueprintLockedException(offenders);
        return true;
    }

    /// <summary>
    ///     Parses the offender list of an import rejection (<c>ExchangeException</c> with message number 6384, AB#6392), whose
    ///     text ends with <c>Locked: {ckTypeId}@{rtId}; …</c>.
    /// </summary>
    public static IReadOnlyList<BlueprintLockOffender> ParseImportOffenders(string message)
    {
        const string marker = "Locked: ";
        var index = message.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return [];
        }

        return message[(index + marker.Length)..]
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(location => ToOffender(location, BlueprintLockReason.EntityLocked, message))
            .ToList();
    }

    private static BlueprintLockOffender ToOffender(OperationMessage message)
    {
        var reason = message.MessageText.Contains(ProtectedAttributesMarker, StringComparison.Ordinal)
            ? BlueprintLockReason.ProtectedAttributes
            : BlueprintLockReason.EntityLocked;
        return ToOffender(message.Location, reason, message.MessageText);
    }

    private static BlueprintLockOffender ToOffender(string? location, BlueprintLockReason reason, string text)
    {
        if (string.IsNullOrEmpty(location))
        {
            return new BlueprintLockOffender(string.Empty, null, reason, text);
        }

        var separator = location.LastIndexOf('@');
        if (separator < 0)
        {
            return new BlueprintLockOffender(location, null, reason, text);
        }

        var rtId = location[(separator + 1)..];
        return new BlueprintLockOffender(location[..separator], rtId.Length == 0 || rtId == "null" ? null : rtId,
            reason, text);
    }
}
