using System.Diagnostics.CodeAnalysis;
using GraphQL;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.Runtime.Contracts.Repositories;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.ReleasedDocuments;

/// <summary>
///     The GraphQL side of the engine's released-document refusals (design note
///     <c>docs/concept-number-ranges-and-released-documents.md</c> of the engine). The engine alone decides; this class only
///     maps its exceptions to the stable error contract, with the engine's code as <c>extensions.code</c>:
///     <list type="bullet">
///         <item><c>RELEASED_DOCUMENT_LOCKED</c> (message 168): a write would change or delete a released document.</item>
///         <item><c>STORNO_REFUSED</c> (messages 169 to 172): a storno the storno rules refuse.</item>
///         <item><c>DOCUMENT_AUDIT_ENTRY_APPEND_ONLY</c> (message 173): a write to an entry of the audit trail.</item>
///     </list>
///     The whole batch is refused, nothing of it is written. Clients read the extensions, never the message text.
/// </summary>
internal static class ReleasedDocumentError
{
    /// <summary>Extension key of the engine message number (of the first refused item).</summary>
    public const string MessageNumberKey = "messageNumber";

    /// <summary>Extension key of the list of refused items.</summary>
    public const string ItemsKey = "items";

    /// <summary>
    ///     Finds a released-document refusal in <paramref name="exception" /> or its inner exceptions and creates its
    ///     GraphQL error.
    /// </summary>
    public static bool TryCreateGraphQlError(Exception exception, [NotNullWhen(true)] out ExecutionError? error)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case ReleasedDocumentLockedException locked:
                    error = CreateLockedError(locked);
                    return true;
                case StornoRefusedException storno:
                    error = CreateMessagesError(storno, StornoRefusedException.ErrorCode, storno.OperationResult);
                    return true;
                case DocumentAuditEntryAppendOnlyException auditEntry:
                    error = CreateMessagesError(auditEntry, DocumentAuditEntryAppendOnlyException.ErrorCode,
                        auditEntry.OperationResult);
                    return true;
            }
        }

        error = null;
        return false;
    }

    /// <summary>
    ///     <c>RELEASED_DOCUMENT_LOCKED</c> with the message number, the first refused document (<c>rtId</c>,
    ///     <c>ckTypeId</c>, <c>operation</c>, <c>numberRange</c>) and <c>items</c> for every refused document; the
    ///     attribute values are never part of it, only the names of what the write would have changed.
    /// </summary>
    internal static ExecutionError CreateLockedError(ReleasedDocumentLockedException exception)
    {
        var items = exception.Refusals
            .Select(r => new Dictionary<string, object?>
            {
                ["rtId"] = r.DocumentId.RtId.ToString(),
                ["ckTypeId"] = r.DocumentId.CkTypeId.ToString(),
                ["operation"] = r.Operation,
                ["changes"] = r.Changes.ToList(),
                ["state"] = r.State,
                ["numberRange"] = r.NumberRange
            })
            .ToList();

        var extensions = new Dictionary<string, object?>
        {
            [MessageNumberKey] = ReleasedDocumentLockedException.MessageNumber,
            [ItemsKey] = items
        };
        if (items.Count > 0)
        {
            foreach (var key in new[] { "rtId", "ckTypeId", "operation", "numberRange" })
            {
                extensions[key] = items[0][key];
            }
        }

        return new ExecutionError(exception.Message, exception)
        {
            Code = ReleasedDocumentLockedException.ErrorCode,
            Extensions = extensions
        };
    }

    /// <summary>
    ///     <c>STORNO_REFUSED</c> or <c>DOCUMENT_AUDIT_ENTRY_APPEND_ONLY</c>: the message number of the first refusal and
    ///     <c>items</c> with every refusal's message number, location (<c>{ckTypeId}@{rtId}</c>) and engine text.
    /// </summary>
    private static ExecutionError CreateMessagesError(Exception exception, string code, OperationResult result)
    {
        var refusals = result.Messages
            .Where(m => m.MessageLevel is MessageLevel.Error or MessageLevel.FatalError)
            .ToList();
        var extensions = new Dictionary<string, object?>
        {
            [ItemsKey] = refusals
                .Select(m => new Dictionary<string, object?>
                {
                    [MessageNumberKey] = m.MessageNumber,
                    ["location"] = m.Location,
                    ["message"] = m.MessageText
                })
                .ToList()
        };
        if (refusals.Count > 0)
        {
            extensions[MessageNumberKey] = refusals[0].MessageNumber;
        }

        return new ExecutionError(exception.Message, exception) { Code = code, Extensions = extensions };
    }
}
