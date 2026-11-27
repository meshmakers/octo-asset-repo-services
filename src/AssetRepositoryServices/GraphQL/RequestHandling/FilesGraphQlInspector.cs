using System.Collections;
using System.Reflection;
using GraphQL;
using GraphQL.Execution;
using GraphQL.Types;
using GraphQLParser.AST;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;

/// <summary>
///     Decides whether a validated GraphQL operation touches System.Files data (AB#6624). Deliberately
///     conservative: a false positive only refuses a foreign-tenant user token, a false negative would leak.
/// </summary>
/// <remarks>
///     An operation touches System.Files when any selected field (a) carries a System.Files CK type id in its
///     metadata (typed query and mutation fields), (b) returns a System.Files graph type (associations that
///     lead to files, also behind inline fragments and fragment spreads), or (c) when any argument value
///     (literal or variable, nested in lists and input objects) names the System.Files model - this covers the
///     generic <c>runtimeEntities</c> queries and mutations (<c>ckId</c> argument, <c>ckTypeId</c> in the input,
///     delete by <c>RtEntityId</c>). A persistent query addressed by rtId (<c>runtimeQuery</c>) cannot be
///     resolved statically and is not covered.
/// </remarks>
internal static class FilesGraphQlInspector
{
    // "System.Files" in any ck id form ("System.Files/Folder", "System.Files-1.0.0/Folder", rt ids).
    private const string ModelMarker = FileSystemConstants.ModelName;

    // GetGraphQlPascalCaseName of a System.Files element: model and element name without separators.
    private const string TypeNamePrefix = "SystemFiles";

    public static bool TouchesFiles(IExecutionContext context)
    {
        var operation = context.Operation;
        if (operation == null)
        {
            return false;
        }

        var root = operation.Operation switch
        {
            OperationType.Mutation => context.Schema.Mutation,
            OperationType.Subscription => context.Schema.Subscription,
            _ => context.Schema.Query
        };

        var visitedFragments = new HashSet<string>(StringComparer.Ordinal);
        return SelectionTouchesFiles(context, operation.SelectionSet, root, visitedFragments);
    }

    private static bool SelectionTouchesFiles(IExecutionContext context, GraphQLSelectionSet? selectionSet,
        IGraphType? parentType, HashSet<string> visitedFragments)
    {
        if (selectionSet == null)
        {
            return false;
        }

        foreach (var selection in selectionSet.Selections)
        {
            switch (selection)
            {
                case GraphQLField field:
                {
                    if (FieldTouchesFiles(context, field, parentType, visitedFragments))
                    {
                        return true;
                    }

                    break;
                }
                case GraphQLInlineFragment inline:
                {
                    var conditionName = inline.TypeCondition?.Type.Name.StringValue;
                    if (IsFilesTypeName(conditionName))
                    {
                        return true;
                    }

                    var fragmentType = conditionName == null ? parentType : context.Schema.AllTypes[conditionName];
                    if (SelectionTouchesFiles(context, inline.SelectionSet, fragmentType ?? parentType,
                            visitedFragments))
                    {
                        return true;
                    }

                    break;
                }
                case GraphQLFragmentSpread spread:
                {
                    var name = spread.FragmentName.Name.StringValue;
                    if (!visitedFragments.Add(name))
                    {
                        break;
                    }

                    var definition = context.Document.Definitions.OfType<GraphQLFragmentDefinition>()
                        .FirstOrDefault(f => f.FragmentName.Name.StringValue == name);
                    if (definition == null)
                    {
                        break;
                    }

                    var conditionName = definition.TypeCondition.Type.Name.StringValue;
                    if (IsFilesTypeName(conditionName) ||
                        SelectionTouchesFiles(context, definition.SelectionSet,
                            context.Schema.AllTypes[conditionName] ?? parentType, visitedFragments))
                    {
                        return true;
                    }

                    break;
                }
            }
        }

        return false;
    }

    private static bool FieldTouchesFiles(IExecutionContext context, GraphQLField field, IGraphType? parentType,
        HashSet<string> visitedFragments)
    {
        if (ArgumentsTouchFiles(context, field.Arguments))
        {
            return true;
        }

        var fieldName = field.Name.StringValue;
        if (fieldName.StartsWith("__", StringComparison.Ordinal))
        {
            return false;
        }

        var fieldType = (parentType as IComplexGraphType)?.GetField(fieldName);
        if (fieldType == null)
        {
            // Unknown here (union parent, or not resolvable): fall back to the name of the field.
            return IsFilesTypeName(fieldName) ||
                   SelectionTouchesFiles(context, field.SelectionSet, null, visitedFragments);
        }

        if (fieldType.Metadata.TryGetValue(Statics.CkId, out var ckId) && IsFilesCkId(ckId))
        {
            return true;
        }

        var returnType = fieldType.ResolvedType?.GetNamedType();
        if (IsFilesTypeName(returnType?.Name))
        {
            return true;
        }

        return SelectionTouchesFiles(context, field.SelectionSet, returnType, visitedFragments);
    }

    /// <summary>
    ///     Argument/field names that carry a ck type id (<c>Statics.CkIdArg</c>/<c>CkIdsArg</c> of the generic
    ///     <c>runtimeEntities</c> connection, and the <c>ckTypeId</c> field of every generic input/id type -
    ///     <c>RtEntityDtoGenericInputType</c>, <c>RtEntityIdType</c>, association targets). Only a value reached
    ///     through one of these keys is tested for the System.Files marker (CodeRabbit finding on PR #61):
    ///     scanning every string argument/filter value for the substring would also match ordinary search text
    ///     or free-form data that happens to contain it, refusing non-Files traffic of a foreign token it must
    ///     not touch. Recursion into lists and objects is unconditional either way, to reach a nested ckTypeId.
    /// </summary>
    private static readonly HashSet<string> CkIdKeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        Statics.CkIdArg, Statics.CkIdsArg, "ckTypeId"
    };

    private static bool ArgumentsTouchFiles(IExecutionContext context, GraphQLArguments? arguments)
    {
        if (arguments == null)
        {
            return false;
        }

        foreach (var argument in arguments)
        {
            var underIdKey = CkIdKeyNames.Contains(argument.Name.StringValue);
            if (ValueTouchesFiles(context, argument.Value, underIdKey))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ValueTouchesFiles(IExecutionContext context, GraphQLValue? value, bool underIdKey)
    {
        switch (value)
        {
            case null:
                return false;
            case GraphQLVariable variable:
            {
                var name = variable.Name.StringValue;
                foreach (var candidate in context.Variables ?? [])
                {
                    if (candidate.Name == name)
                    {
                        return ObjectTouchesFiles(candidate.Value, underIdKey);
                    }
                }

                return false;
            }
            case GraphQLListValue list:
                return list.Values != null && list.Values.Any(v => ValueTouchesFiles(context, v, underIdKey));
            case GraphQLObjectValue obj:
                return obj.Fields != null && obj.Fields.Any(f =>
                    ValueTouchesFiles(context, f.Value, CkIdKeyNames.Contains(f.Name.StringValue)));
            case GraphQLStringValue str:
                return underIdKey && ContainsModelMarker(str.Value.ToString());
            case GraphQLEnumValue en:
                return underIdKey && ContainsModelMarker(en.Name.StringValue);
            default:
                return false;
        }
    }

    private const int MaxDepth = 8;

    private static bool ObjectTouchesFiles(object? value, bool underIdKey, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            // Deeper than any real input: fail closed for the (foreign) caller.
            return true;
        }

        switch (value)
        {
            case null:
                return false;
            case string s:
                return underIdKey && ContainsModelMarker(s);
            case RtCkId<CkTypeId> typeId:
                // Already a resolved ck type id, not raw text the caller controls arbitrarily - always exact.
                return FileSystemService.IsFileSystemType(typeId);
            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    var childUnderIdKey = entry.Key is string keyName && CkIdKeyNames.Contains(keyName);
                    if (ObjectTouchesFiles(entry.Value, childUnderIdKey, depth + 1))
                    {
                        return true;
                    }
                }

                return false;
            case IEnumerable enumerable:
                foreach (var item in enumerable)
                {
                    if (ObjectTouchesFiles(item, underIdKey, depth + 1))
                    {
                        return true;
                    }
                }

                return false;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is decimal or DateTime or DateTimeOffset or TimeSpan or Guid)
        {
            return false;
        }

        // A coerced input object (RtEntityIdDto, RtEntityDto, ...) reached without a ckTypeId/ckId key wrapping
        // it directly: still walk its properties by name, so a ckTypeId *property* one level down is found.
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0 || !property.CanRead)
            {
                continue;
            }

            object? propertyValue;
            try
            {
                propertyValue = property.GetValue(value);
            }
            catch (Exception)
            {
                continue;
            }

            var childUnderIdKey = CkIdKeyNames.Contains(property.Name);
            if (ObjectTouchesFiles(propertyValue, childUnderIdKey, depth + 1))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFilesCkId(object? ckId) => ckId switch
    {
        RtCkId<CkTypeId> typeId => FileSystemService.IsFileSystemType(typeId),
        _ => ckId != null && ContainsModelMarker(ckId.ToString())
    };

    private static bool IsFilesTypeName(string? name) =>
        name != null && (name.StartsWith(TypeNamePrefix, StringComparison.OrdinalIgnoreCase) ||
                         name.Contains(ModelMarker, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsModelMarker(string? text) =>
        text != null && text.Contains(ModelMarker, StringComparison.OrdinalIgnoreCase);
}
