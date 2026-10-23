using System.Runtime.CompilerServices;
using GraphQL;
using GraphQL.Types;
using GraphQL.Validation;
using GraphQLParser.AST;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;

/// <summary>
///     AB#7020 (OD-1 option C + OD-2 option A, ONE rule): a mutation request carries either generic mutations or exactly
///     one CK method call. A request that breaks the rule is refused as a whole in the validation phase, i.e. before any
///     resolver ran, so there is no half-applied request and no method on an entity that is not committed yet.
/// </summary>
/// <remarks>
///     <para>
///         Only the <b>selected</b> mutation operation is judged (queries, subscriptions and introspection never).
///         Aliases, fragment spreads and inline fragments are resolved; the same fragment spread twice on one level is
///         one field (as in execution). Directives (<c>@skip</c>/<c>@include</c>) are not evaluated: a field counts
///         whether or not it is skipped at runtime (conservative).
///     </para>
///     <para>
///         A method field is recognized by its metadata marker <see cref="Statics.CkMethodCall" /> (AB#5670), never by
///         its name. "Generic mutation" is every other work field of the mutation tree; navigation fields of the tree
///         (<c>runtime</c>, <c>sampleAccounts</c>, ...) are object types that the query tree does not reach, they carry
///         no work of their own and are walked through.
///     </para>
///     <para>
///         The refusal is ONE error with <c>extensions.code</c> = <see cref="Statics.GraphQlOneMethodCallPerRequest" />
///         and <c>extensions.parameters</c> (<c>reason</c>, <c>methodIds</c>); the message is fixed, so two requests that
///         differ only in argument values get a byte-identical error. The code is not part of the method response
///         envelope (<c>METHOD_*</c>).
///     </para>
/// </remarks>
internal sealed class OneMethodCallPerRequestRule : IValidationRule
{
    /// <summary>Fixed, language-neutral message.</summary>
    public const string Message = "A mutation request carries either generic mutations or exactly one CK method call.";

    /// <summary>Parameter: why the request was refused.</summary>
    public const string ReasonParameter = "reason";

    /// <summary>Parameter: the qualified ids of all method fields of the request, sorted, comma separated.</summary>
    public const string MethodIdsParameter = "methodIds";

    /// <summary>Reason: a method call next to a generic mutation.</summary>
    public const string ReasonMixedWithGeneric = "MIXED_WITH_GENERIC_MUTATION";

    /// <summary>Reason: two or more method calls.</summary>
    public const string ReasonMultipleMethods = "MULTIPLE_METHOD_CALLS";

    private const int MaxDepth = 64;

    // Object types reachable from the query root, per schema (a schema is immutable once initialized).
    private static readonly ConditionalWeakTable<ISchema, HashSet<string>> QueryReachableTypes = new();

    /// <summary>
    ///     THE predicate: does this field count as a method call for the rule? Today every generated CK method field
    ///     counts, async methods included. AB#7021 (async methods) changes only this function.
    /// </summary>
    internal static bool CountsAsMethodCall(FieldType field) =>
        field.GetMetadata<CkMethodCallInfo?>(Statics.CkMethodCall) != null;

    /// <inheritdoc />
    public ValueTask<INodeVisitor?> GetPreNodeVisitorAsync(ValidationContext context)
    {
        Validate(context);
        return default;
    }

    /// <inheritdoc />
    public ValueTask<INodeVisitor?> GetPostNodeVisitorAsync(ValidationContext context) => default;

    /// <inheritdoc />
    public ValueTask<IVariableVisitor?> GetVariableVisitorAsync(ValidationContext context) => default;

    private static void Validate(ValidationContext context)
    {
        var operation = context.Operation;
        if (operation == null || operation.Operation != OperationType.Mutation || context.Schema.Mutation == null)
        {
            return;
        }

        var collector = new Collector(context, QueryReachableTypes.GetValue(context.Schema, ReachableFromQuery));
        collector.Collect(operation.SelectionSet, context.Schema.Mutation, 0);

        if (collector.MethodIds.Count == 0)
        {
            return;
        }

        string? reason = null;
        if (collector.MethodIds.Count > 1)
        {
            reason = ReasonMultipleMethods;
        }
        else if (collector.GenericFieldCount > 0)
        {
            reason = ReasonMixedWithGeneric;
        }

        if (reason != null)
        {
            context.ReportError(CreateError(reason, collector.MethodIds));
        }
    }

    private static ValidationError CreateError(string reason, List<string> methodIds)
    {
        var parameters = new Dictionary<string, object?>
        {
            [MethodIdsParameter] = string.Join(',', methodIds.OrderBy(m => m, StringComparer.Ordinal)),
            [ReasonParameter] = reason
        };
        return new ValidationError(Message)
        {
            Code = Statics.GraphQlOneMethodCallPerRequest,
            Extensions = new Dictionary<string, object?>
            {
                [CkMethodGraphQlError.CodeKey] = Statics.GraphQlOneMethodCallPerRequest,
                [CkMethodGraphQlError.ParametersKey] = parameters
            }
        };
    }

    private static HashSet<string> ReachableFromQuery(ISchema schema)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<IGraphType>();
        if (schema.Query != null)
        {
            stack.Push(schema.Query);
        }

        while (stack.Count > 0)
        {
            var type = stack.Pop();
            if (!seen.Add(type.Name))
            {
                continue;
            }

            if (type is IComplexGraphType complex)
            {
                foreach (var field in complex.Fields)
                {
                    var named = field.ResolvedType?.GetNamedType();
                    if (named != null)
                    {
                        stack.Push(named);
                    }
                }
            }

            if (type is IAbstractGraphType abstractType && abstractType.PossibleTypes != null)
            {
                foreach (var possible in abstractType.PossibleTypes)
                {
                    stack.Push(possible);
                }
            }
        }

        return seen;
    }

    private sealed class Collector(ValidationContext context, HashSet<string> queryReachable)
    {
        private readonly HashSet<string> _fragmentsOnPath = new(StringComparer.Ordinal);

        public List<string> MethodIds { get; } = [];

        public int GenericFieldCount { get; private set; }

        public void Collect(GraphQLSelectionSet? selectionSet, IGraphType? parentType, int depth) =>
            Collect(selectionSet, parentType, depth, new HashSet<string>(StringComparer.Ordinal));

        private void Collect(GraphQLSelectionSet? selectionSet, IGraphType? parentType, int depth,
            HashSet<string> spreadsOfThisLevel)
        {
            if (selectionSet == null || depth > MaxDepth)
            {
                return;
            }

            foreach (var selection in selectionSet.Selections)
            {
                switch (selection)
                {
                    case GraphQLField field:
                        VisitField(field, parentType, depth);
                        break;
                    case GraphQLInlineFragment inline:
                    {
                        var conditionName = inline.TypeCondition?.Type.Name.StringValue;
                        var fragmentType = conditionName == null ? parentType : context.Schema.AllTypes[conditionName];
                        Collect(inline.SelectionSet, fragmentType ?? parentType, depth, spreadsOfThisLevel);
                        break;
                    }
                    case GraphQLFragmentSpread spread:
                    {
                        var name = spread.FragmentName.Name.StringValue;
                        if (!spreadsOfThisLevel.Add(name) || !_fragmentsOnPath.Add(name))
                        {
                            break;
                        }

                        var definition = context.Document.Definitions.OfType<GraphQLFragmentDefinition>()
                            .FirstOrDefault(f => f.FragmentName.Name.StringValue == name);
                        if (definition != null)
                        {
                            var conditionName = definition.TypeCondition.Type.Name.StringValue;
                            Collect(definition.SelectionSet, context.Schema.AllTypes[conditionName] ?? parentType,
                                depth, spreadsOfThisLevel);
                        }

                        _fragmentsOnPath.Remove(name);
                        break;
                    }
                }
            }
        }

        private void VisitField(GraphQLField field, IGraphType? parentType, int depth)
        {
            var name = field.Name.StringValue;
            if (name.StartsWith("__", StringComparison.Ordinal))
            {
                return;
            }

            var fieldType = (parentType as IComplexGraphType)?.GetField(name);
            if (fieldType == null)
            {
                // Unknown field: another validation rule reports it.
                return;
            }

            var info = CountsAsMethodCall(fieldType)
                ? fieldType.GetMetadata<CkMethodCallInfo>(Statics.CkMethodCall)
                : null;
            if (info != null)
            {
                MethodIds.Add(info.QualifiedMethodId);
                return;
            }

            var returnType = fieldType.ResolvedType?.GetNamedType();
            if (returnType is IObjectGraphType && !queryReachable.Contains(returnType.Name))
            {
                // Navigation inside the mutation tree: carries no work of its own.
                var before = MethodIds.Count + GenericFieldCount;
                Collect(field.SelectionSet, returnType, depth + 1);
                if (MethodIds.Count + GenericFieldCount == before)
                {
                    // Nothing found below (a mutation-only result type selected by scalars only, or an empty
                    // navigation): the field itself is the work.
                    GenericFieldCount++;
                }

                return;
            }

            GenericFieldCount++;
        }
    }
}
