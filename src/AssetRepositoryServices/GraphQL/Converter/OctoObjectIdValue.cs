using System.Diagnostics;
using GraphQLParser;
using GraphQLParser.AST;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Converter;

[DebuggerDisplay("GraphQLIntValue: {Value}")]
internal class OctoObjectIdValue : GraphQLValue, IHasValueNode
{
    /// <summary>
    ///     Creates a new instance with the specified value.
    /// </summary>
    public OctoObjectIdValue(ROM value)
    {
        Value = value;
    }

    public OctoObjectIdValue(OctoObjectId octoObjectId)
    {
        Value = octoObjectId.ToString();
    }

    /// <inheritdoc />
    public override ASTNodeKind Kind => ASTNodeKind.IntValue;

    /// <summary>
    ///     Integer value represented as <see cref="ROM" />.
    /// </summary>
    public ROM Value { get; }
}
