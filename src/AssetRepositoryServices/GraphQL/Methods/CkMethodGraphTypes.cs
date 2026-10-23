using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;

/// <summary>The value of a successful method call field: the invocation id and the declared result (if any).</summary>
/// <param name="InvocationId">Id of the invocation, for correlation with the handler log.</param>
/// <param name="Value">The declared result converted for the result field; <c>null</c> for a method without result.</param>
internal sealed record CkMethodCallResult(Guid InvocationId, object? Value);

/// <summary>
///     AB#5670: the GraphQL input and result type of one CK method. Shared by every concrete type that has the
///     method, so an inherited method adds one pair of types, not one per derived type. Names are built from the
///     declaring element and the method (<c>{Declaring}{Method}MethodInput</c> / <c>…MethodResult</c>; a method
///     version above 1 is appended without the dash, like element versions).
/// </summary>
/// <param name="Input">Input type with one field per declared parameter; <c>null</c> for a method without parameters.</param>
/// <param name="InputRequired"><c>true</c> when at least one parameter is required, so the <c>input</c> argument is non-null.</param>
/// <param name="Result">Result type: <c>invocationId</c> and, for a method with a declared result, <c>result</c>.</param>
internal sealed record CkMethodGraphTypes(
    InputObjectGraphType? Input,
    bool InputRequired,
    ObjectGraphType<CkMethodCallResult> Result)
{
    /// <summary>Name of the result member.</summary>
    internal const string ResultField = "result";

    /// <summary>Name of the invocation id member.</summary>
    internal const string InvocationIdField = "invocationId";

    internal static CkMethodGraphTypes Create(IGraphTypesCache graphTypesCache, CkMethodGraph method)
    {
        ArgumentNullException.ThrowIfNull(graphTypesCache);
        ArgumentNullException.ThrowIfNull(method);

        var definition = method.Definition;
        var baseName = method.DeclaringCkTypeId.ToRtCkId().GetGraphQlPascalCaseName() +
                       CkMethodIds.MethodClassName(definition.MethodId);
        var description = $"CK method '{method.QualifiedMethodId}'.";

        InputObjectGraphType? input = null;
        var inputRequired = false;
        if (definition.Parameters is { Count: > 0 })
        {
            input = new InputObjectGraphType
            {
                Name = baseName + "MethodInput",
                Description = $"Parameters of the {description}"
            };
            foreach (var parameter in definition.Parameters)
            {
                var (graphType, type) = OctoValueTypeMapper.GetValueFieldType(graphTypesCache, parameter.ValueType,
                    parameter.ValueCkEnumId, parameter.ValueCkRecordId, parameter.Name, isInputType: true);
                var field = new FieldType
                {
                    Name = parameter.Name,
                    Description = parameter.Description,
                    ResolvedType = parameter.IsOptional ? graphType : Wrap(graphType),
                    Type = graphType != null ? null : parameter.IsOptional ? type : Wrap(type!)
                };
                input.AddField(field);
                inputRequired |= !parameter.IsOptional;
            }
        }

        var result = new ObjectGraphType<CkMethodCallResult>
        {
            Name = baseName + "MethodResult",
            Description = $"Result of a successful call of the {description} A failed call is one GraphQL error."
        };
        result.Field<NonNullGraphType<IdGraphType>>(InvocationIdField)
            .Description("Id of the invocation, for correlation with the handler log.")
            .Resolve(ctx => ctx.Source.InvocationId.ToString("D"));
        if (definition.Result != null)
        {
            var (graphType, type) = OctoValueTypeMapper.GetValueFieldType(graphTypesCache, definition.Result.ValueType,
                definition.Result.ValueCkEnumId, definition.Result.ValueCkRecordId, ResultField, isInputType: false);
            result.AddField(new FieldType
            {
                Name = ResultField,
                Description = "The declared result of the method.",
                ResolvedType = graphType,
                Type = type,
                Resolver = new global::GraphQL.Resolvers.FuncFieldResolver<CkMethodCallResult, object>(ctx =>
                    ctx.Source.Value)
            });
        }

        return new CkMethodGraphTypes(input, inputRequired, result);
    }

    private static IGraphType? Wrap(IGraphType? graphType) =>
        graphType == null ? null : new NonNullGraphType(graphType);

    private static Type Wrap(Type type) => typeof(NonNullGraphType<>).MakeGenericType(type);
}
