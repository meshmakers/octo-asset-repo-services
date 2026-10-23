using GraphQL;
using GraphQL.Resolvers;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;

/// <summary>
///     AB#5670 (F3.7): adds one typed mutation field per CK method to the per-type mutation type
///     (<c>runtime { {type}s { {method}(rtId, input) } }</c>), next to <c>create</c> and <c>update</c>. Instance methods
///     take the target <c>rtId</c>, static methods none; the <c>input</c> argument is built from the declared
///     parameters and omitted for a method without parameters; the field type is the generated result type. A type
///     without methods gets no field, so its schema members do not change.
/// </summary>
internal static class CkMethodMutationFields
{
    /// <summary>
    ///     Name of the GraphQL field of a method: the method name without the version dash, camelCase; a version
    ///     above 1 is appended (<c>ResetPassword-2</c> becomes <c>resetPassword2</c>).
    /// </summary>
    public static string FieldName(string methodId) => Meshmakers.Common.Shared.StringExtensions.ToCamelCase(CkMethodIds.MethodClassName(methodId));

    /// <summary>
    ///     Adds the method fields of <paramref name="methods" /> (all methods of the concrete type including the
    ///     inherited ones) to <paramref name="mutationType" />.
    /// </summary>
    /// <exception cref="OctoGraphQLException">A field name clashes with an existing field or with another method.</exception>
    public static void Add(ObjectGraphType mutationType, RtCkId<CkTypeId> rtCkTypeId,
        IEnumerable<CkMethodGraph> methods, Func<CkMethodGraph, CkMethodGraphTypes> typesOf)
    {
        ArgumentNullException.ThrowIfNull(mutationType);
        ArgumentNullException.ThrowIfNull(rtCkTypeId);
        ArgumentNullException.ThrowIfNull(methods);
        ArgumentNullException.ThrowIfNull(typesOf);

        foreach (var method in methods.OrderBy(m => m.Definition.MethodId, StringComparer.Ordinal))
        {
            var fieldName = FieldName(method.Definition.MethodId);
            if (mutationType.Fields.Any(f => string.Equals(f.Name, fieldName, StringComparison.Ordinal)))
            {
                throw OctoGraphQLException.CkMethodFieldNameClash(rtCkTypeId.SemanticVersionedFullName,
                    method.QualifiedMethodId, fieldName);
            }

            var types = typesOf(method);
            var arguments = new QueryArguments();
            if (method.Definition.Kind == CkMethodKindDto.Instance)
            {
                arguments.Add(new QueryArgument(new NonNullGraphType(new OctoObjectIdType()))
                {
                    Name = Statics.RtIdArg,
                    Description = "Runtime id of the target entity."
                });
            }

            if (types.Input != null)
            {
                arguments.Add(new QueryArgument(types.InputRequired
                    ? new NonNullGraphType(types.Input)
                    : types.Input)
                {
                    Name = Statics.InputArg,
                    Description = "The parameters of the method."
                });
            }

            var field = new FieldType
            {
                Name = fieldName,
                Description = Describe(method),
                ResolvedType = types.Result,
                Arguments = arguments,
                Resolver = new FuncFieldResolver<object?, object?>(CkMethodFieldResolver.ResolveAsync)
            };
            field.Metadata[Statics.CkId] = rtCkTypeId;
            field.Metadata[Statics.CkMethodCall] = new CkMethodCallInfo(rtCkTypeId, method.Definition.MethodId,
                method.QualifiedMethodId, method.Definition.Kind);
            mutationType.AddField(field);
        }
    }

    private static string Describe(CkMethodGraph method)
    {
        var text = $"Calls the CK method '{method.QualifiedMethodId}'.";
        return string.IsNullOrWhiteSpace(method.Definition.Description)
            ? text
            : $"{method.Definition.Description} {text}";
    }
}

/// <summary>
///     The resolver of the generated method fields. AB#5670 only maps a response to the field value or the error;
///     the resolver that authorizes through the gateway, loads the target and dispatches is AB#7026. Until then
///     every call answers <c>METHOD_HANDLER_NOT_AVAILABLE</c> without touching any target, parameter or handler.
/// </summary>
internal static class CkMethodFieldResolver
{
    /// <summary>Fixed reason of the placeholder answer until the gateway resolver (AB#7026) is wired.</summary>
    public const string GatewayPendingReason = "GATEWAY_RESOLVER_PENDING";

    public static ValueTask<object?> ResolveAsync(IResolveFieldContext<object?> context)
    {
        var info = context.FieldDefinition.GetMetadata<CkMethodCallInfo>(Statics.CkMethodCall);
        var response = CkMethodResponse.Failed(Guid.NewGuid(), string.Empty,
            CkMethodError.Create(CkMethodErrorCodes.MethodHandlerNotAvailable,
                (CkMethodErrorParameters.MethodId, info.QualifiedMethodId),
                (CkMethodErrorParameters.Reason, GatewayPendingReason)));
        context.Errors.Add(CkMethodGraphQlError.Create(response.Error!));
        return ValueTask.FromResult<object?>(null);
    }
}
