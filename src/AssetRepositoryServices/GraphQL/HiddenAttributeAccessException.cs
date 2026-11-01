using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     CK v2 (AB#5668): an attribute whose <c>access</c> forbids the attempted operation was used through the generic
///     GraphQL surface. Mapped by <c>HandleException</c> to <see cref="Statics.GraphQlAttributeNotWritable" /> (writes)
///     or <see cref="Statics.GraphQlAttributeNotQueryable" /> (queries). The message names the attribute, never a value.
/// </summary>
internal sealed class HiddenAttributeAccessException : Exception
{
    private HiddenAttributeAccessException(string message, string attributePath, string entityName,
        CkAttributeAccessDto? access, string operation, bool isWrite)
        : base(message)
    {
        AttributePath = attributePath;
        EntityName = entityName;
        Access = access;
        Operation = operation;
        IsWrite = isWrite;
    }

    public string AttributePath { get; }
    public string EntityName { get; }
    public CkAttributeAccessDto? Access { get; }
    public string Operation { get; }
    public bool IsWrite { get; }

    /// <summary>
    ///     Why the write is refused when it is not the CK <c>access</c> of the attribute: <c>identity-owned</c> (AB#8042,
    ///     <see cref="Meshmakers.Octo.ConstructionKit.Contracts.IdentityOwnedWrites" />). Null for an access refusal.
    /// </summary>
    public string? Reason { get; private init; }

    public string Code => IsWrite ? Statics.GraphQlAttributeNotWritable : Statics.GraphQlAttributeNotQueryable;

    /// <summary>
    ///     AB#8042: an identity-owned User field or an identity association, which only the identity service writes (its REST
    ///     API and the identity methods). Same code as an access refusal (<c>ATTRIBUTE_NOT_WRITABLE</c>), extension
    ///     <c>reason = identity-owned</c>.
    /// </summary>
    public static HiddenAttributeAccessException IdentityOwned(string attributePath, string entityName)
    {
        attributePath = RedactSelectorValues(attributePath);
        return new HiddenAttributeAccessException(
            $"'{attributePath}' of '{entityName}' is written only by the identity service (identity REST API or identity " +
            "methods), not via generic mutations or query rows.",
            attributePath, entityName, null, "write", true)
        {
            Reason = Meshmakers.Octo.ConstructionKit.Contracts.IdentityOwnedWrites.RefusalReason
        };
    }

    /// <summary>
    ///     The contract message of the write rejection (contract §4.1).
    /// </summary>
    public static HiddenAttributeAccessException NotWritable(string attributeName, string entityName,
        CkAttributeAccessDto access)
    {
        attributeName = RedactSelectorValues(attributeName);
        return new HiddenAttributeAccessException(
            $"Attribute '{attributeName}' of '{entityName}' is not writable via generic mutations (access: {access}).",
            attributeName, entityName, access, "write", true);
    }

    public static HiddenAttributeAccessException NotQueryable(string attributePath, string entityName,
        string operation)
    {
        attributePath = RedactSelectorValues(attributePath);
        return new HiddenAttributeAccessException(
            $"Attribute '{attributePath}' of '{entityName}' is hidden and cannot be used for {operation}.",
            attributePath, entityName, CkAttributeAccessDto.Hidden, operation, false);
    }

    /// <summary>
    ///     F1.5-S4 (AB#5923): an access error never carries a value — not even the caller's own guess in an entity
    ///     selector (<c>members.type[passwordHash='X']-&gt;name</c> becomes <c>members.type[passwordHash=…]-&gt;name</c>),
    ///     because error texts end up in logs and client error reports.
    /// </summary>
    /// <remarks>
    ///     AB#8052: everything from the first <c>=</c> inside a selector up to the <b>last</b> <c>]</c> of the path is
    ///     replaced (the rule of the mesh adapter's <c>RtAttributeAccessGuard.WithoutSelectorValues</c>, AB#8049). A value
    ///     may itself contain <c>]</c> or <c>[</c> (<c>[passwordHash='a]b']</c>); ending the redaction at the first
    ///     <c>]</c>, as before, left the rest of the value in the message. The price: path segments between the first
    ///     selector value and the last <c>]</c> are not shown either (<c>a.t[x=1].b.u[y=2]-&gt;c</c> becomes
    ///     <c>a.t[x=…]-&gt;c</c>). A <c>[</c> without a following <c>=</c> (<c>items[0].name</c>) is no selector and is kept.
    ///     A selector without a closing <c>]</c> after its <c>=</c> is redacted to the end of the path.
    /// </remarks>
    internal static string RedactSelectorValues(string path)
    {
        var open = path.IndexOf('[');
        var equals = open < 0 ? -1 : path.IndexOf('=', open);
        var close = path.LastIndexOf(']');
        if (equals < 0)
        {
            return path;
        }

        return close < equals ? path[..equals] + "=…" : path[..equals] + "=…" + path[close..];
    }
}
