namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     An argument of a runtime query is missing or malformed. Mapped to the stable error code
///     <c>INVALID_ARGUMENT</c> with <c>extensions.argument</c>; the message text is English and not a contract.
/// </summary>
internal sealed class QueryArgumentException(string argument, string message) : Exception(message)
{
    public string Argument { get; } = argument;
}
