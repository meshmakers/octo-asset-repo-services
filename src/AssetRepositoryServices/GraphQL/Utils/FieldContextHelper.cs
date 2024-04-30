using System.Collections;
using GraphQL;
using GraphQL.Execution;
using GraphQL.Types;
using GraphQLParser.AST;
using ExecutionContext = GraphQL.Execution.ExecutionContext;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

/// <summary>
/// Helper class that parses the AST of sub fields. We need this for stream data queries to get the
/// all information of sub fields WHILE resolving the parent field.
/// Imagine the following query:
/// <code>
/// query {
/// streamData {
///     tsIndustryEnergyEnergyMeter(first: 3) {
///         items {
///             rtId
///                 ckTypeId
///             timeStamp
///             avg_voltage: voltage(
///                 timeSeriesFilter: { aggregationType: AVERAGE, interval: 1 }
///             )
///         }
///     }
/// }
/// }
/// </code>
/// When tsIndustryEnergyEnergyMeter is resolved, we need to know that the field voltage has the arguments.
/// </summary>
/// <param name="ExecutionContext"></param>
/// <param name="FiledDefinition"></param>
/// <param name="FieldAst"></param>
internal record FieldContext(
    ExecutionContext ExecutionContext,
    FieldType FiledDefinition,
    GraphQLField FieldAst
)
{
    private static readonly GetAroundProtectedFunctions ProtectedFunctionAccessor = new();

    public string Name => FieldAst.Name.StringValue;
    public string AliasOrName => FieldAst.Alias?.Name.StringValue ?? Name;

    /// <summary>
    /// Factory method to create a <see cref="FieldContext"/> from a <see cref="IResolveFieldContext"/>
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    public static FieldContext FromContext(IResolveFieldContext context)
    {
        return new FieldContext(
            FieldAst: context.FieldAst,
            FiledDefinition: GetFieldDefinition(context.Schema, context.ParentType, context.FieldAst)!,
            ExecutionContext: new ExecutionContext
            {
                Document = context.Document,
                Schema = context.Schema,
                Variables = context.Variables
            }
        );
    }

    private IEnumerable<FieldContext> GetFields()
    {
        if (FiledDefinition.ResolvedType is null)
        {
            yield break;
        }

        if (ResolveType(FiledDefinition.ResolvedType) is not IObjectGraphType objectGraph)
        {
            yield break;
        }

        var fields = GetSubFields(ExecutionContext, objectGraph, FieldAst);
        if (fields is null)
        {
            yield break;
        }

        foreach (var field in fields)
        {
            var fieldDefinition = GetFieldDefinition(ExecutionContext.Schema, objectGraph, field.Value.field);
            if (fieldDefinition is null)
            {
                continue;
            }

            yield return new FieldContext
            (
                FieldAst: field.Value.field,
                FiledDefinition: fieldDefinition,
                ExecutionContext: ExecutionContext
            );
        }
    }


    public IEnumerable<FieldContext> Fields => GetFields();

    public object? GetArgumentObject(string name)
    {
        var field = FieldAst.Arguments?.FirstOrDefault(a => a.Name == name);
        var valueValue = field?.Value;

        var filedDefinition = FiledDefinition.Arguments?.FirstOrDefault(x => x.Name == name);
        if (filedDefinition?.ResolvedType is null)
        {
            return null;
        }

        var argVal = ExecutionHelper.CoerceValue(filedDefinition.ResolvedType, valueValue, ExecutionContext.Variables,
            filedDefinition.DefaultValue);
        return argVal.Value;
    }

    public IEnumerable<T> GetArgumentList<T>(string name)
    {
        var obj = GetArgumentObject(name);
        if (obj is null)
        {
            yield break;
        }

        var objs = (IEnumerable)obj;
        foreach (var o in objs)
        {
            yield return (T)o;
        }
    }

    private IGraphType ResolveType(IGraphType type) =>
        type switch
        {
            NonNullGraphType nullType => ResolveType(nullType.ResolvedType!),
            ListGraphType listType => ResolveType(listType.ResolvedType!),
            _ => type
        };


    private static Dictionary<string, (GraphQLField field, FieldType fieldType)>? GetSubFields(ExecutionContext context,
        IGraphType graphType, GraphQLField? field)
    {
        if (field == null || !(field.SelectionSet?.Selections.Count > 0))
        {
            return null;
        }

        return ProtectedFunctionAccessor.PublicCollectFieldsFrom(
            context, graphType, field.SelectionSet, null);
    }

    private static FieldType? GetFieldDefinition(ISchema schema, IObjectGraphType parentType, GraphQLField field)
    {
        return ProtectedFunctionAccessor.PublicGetFieldDefinition(schema, parentType, field);
    }

    /// <summary>
    /// Helper class that sole purpose is to get access to protected functions <see cref="ExecutionStrategy"/>
    /// We need this, because the execution strategy already parses the AST and we don't want to do this again.s
    /// </summary>
    private class GetAroundProtectedFunctions : ExecutionStrategy
    {
        public override Task ExecuteNodeTreeAsync(ExecutionContext context, ExecutionNode rootNode)
        {
            // we don't need this. We only use this implementation to get access to protected functions of the base class
            throw new NotImplementedException();
        }

        public Dictionary<string, (GraphQLField field, FieldType fieldType)>? PublicCollectFieldsFrom(
            ExecutionContext context, IGraphType specificType, GraphQLSelectionSet selectionSet,
            Dictionary<string, (GraphQLField field, FieldType fieldType)>? fields)
        {
            return base.CollectFieldsFrom(context, specificType, selectionSet, fields);
        }

        public FieldType? PublicGetFieldDefinition(ISchema schema, IObjectGraphType parentType, GraphQLField field)
        {
            return GetFieldDefinition(schema, parentType, field);
        }
    }
}

internal static class FieldContextStructExtensions
{
    public static T? GetArgument<T>(this FieldContext con, string name)
        where T : struct => ValueConverter.ConvertTo<T?>(con.GetArgumentObject(name));
}

internal static class FieldContextClassExtensions
{
    public static T? GetArgument<T>(this FieldContext con, string name)
        where T : class => ValueConverter.ConvertTo<T?>(con.GetArgumentObject(name));
}