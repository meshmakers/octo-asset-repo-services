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

    public string Code => IsWrite ? Statics.GraphQlAttributeNotWritable : Statics.GraphQlAttributeNotQueryable;

    /// <summary>
    ///     The contract message of the write rejection (contract §4.1).
    /// </summary>
    public static HiddenAttributeAccessException NotWritable(string attributeName, string entityName,
        CkAttributeAccessDto access)
    {
        return new HiddenAttributeAccessException(
            $"Attribute '{attributeName}' of '{entityName}' is not writable via generic mutations (access: {access}).",
            attributeName, entityName, access, "write", true);
    }

    public static HiddenAttributeAccessException NotQueryable(string attributePath, string entityName,
        string operation)
    {
        return new HiddenAttributeAccessException(
            $"Attribute '{attributePath}' of '{entityName}' is hidden and cannot be used for {operation}.",
            attributePath, entityName, CkAttributeAccessDto.Hidden, operation, false);
    }
}
