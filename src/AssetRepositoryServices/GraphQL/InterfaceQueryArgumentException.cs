namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     AB#6932: an argument of <c>runtime.byInterface</c> is malformed. Mapped to the stable error code
///     <c>INVALID_ARGUMENT</c> with <c>extensions.argument</c>; the message text is English and not a contract.
/// </summary>
internal sealed class InterfaceQueryArgumentException(string argument, string message) : Exception(message)
{
    public string Argument { get; } = argument;
}
