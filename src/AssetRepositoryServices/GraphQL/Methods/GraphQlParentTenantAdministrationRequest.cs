using System.Text.Json;
using GraphQLParser;
using GraphQLParser.AST;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Meshmakers.Octo.Services.Methods.Authorization;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;

/// <summary>
///     AB#8986: the HTTP tenant gate's per-request predicate for <c>/tenants/{tenantId}/graphQl</c>
///     (<see cref="IAllowParentTenantAdministrationRequest" />). It answers <c>true</c> only for a request whose operations are
///     <b>exclusively</b> calls of the closed method list <see cref="CkParentTenantAdministrationMethods" />
///     (<c>mutation { runtime { systemBotTenantRepositorys { dump(...) {...} } } }</c>) or reads of <c>runtime.operations</c>;
///     the gate then lets a user token of a parent tenant through to a registered child tenant. The method gateway and the
///     operation reader check the caller again (method rule, records of the closed list only).
/// </summary>
/// <remarks>
///     Fail-closed by construction — <c>false</c> for anything it does not fully classify:
///     <list type="bullet">
///         <item>only <c>POST</c> with a JSON body of at most <see cref="MaxBodyBytes" /> bytes and no query string (the server
///             reads <c>query</c> from the query string on POST too); a batch (JSON array), a form or multipart body is refused;</item>
///         <item>the body holds only <c>query</c> (required), <c>variables</c>, <c>operationName</c> and an empty
///             <c>extensions</c> (no persisted documents);</item>
///         <item>every definition of the document is a query or mutation operation (no fragment definitions, no
///             subscription), so a request that mixes an allowed and a forbidden field in any operation is refused as a whole;</item>
///         <item>down to the method field, every selection is a field (no fragment spread, no inline fragment) whose <b>name</b>
///             (never the alias) is allowed: <c>runtime</c>, then the type container and the method field of the list (mutation)
///             or <c>operations</c> (query). Below that the selection is the result of the method or the operation record.</item>
///     </list>
///     The body is buffered and rewound, so the GraphQL endpoint reads it again.
/// </remarks>
internal sealed class GraphQlParentTenantAdministrationRequest : IAllowParentTenantAdministrationRequest
{
    /// <summary>Largest body that is classified; a larger one is refused (an administration call is small).</summary>
    internal const int MaxBodyBytes = 64 * 1024;

    internal const string RuntimeFieldName = "runtime";

    private static readonly HashSet<string> AllowedBodyProperties =
        new(["query", "variables", "operationName", "extensions"], StringComparer.Ordinal);

    /// <summary>Type container field (e.g. <c>systemBotTenantRepositorys</c>) → allowed method fields (e.g. <c>dump</c>).</summary>
    internal static IReadOnlyDictionary<string, HashSet<string>> MutationFields { get; } = BuildMutationFields();

    public async ValueTask<bool> IsParentTenantAdministrationRequestAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;
        if (!HttpMethods.IsPost(request.Method) || request.QueryString.HasValue ||
            request.ContentLength is > MaxBodyBytes ||
            !IsJson(request.ContentType))
        {
            return false;
        }

        var body = await ReadBodyAsync(request, context.RequestAborted).ConfigureAwait(false);
        if (body is null || !TryReadQuery(body, out var query))
        {
            return false;
        }

        return IsAdministrationDocument(query);
    }

    /// <summary>Classifies one GraphQL document (public for the tests).</summary>
    internal static bool IsAdministrationDocument(string query)
    {
        GraphQLDocument document;
        try
        {
            document = Parser.Parse(query);
        }
        catch (Exception)
        {
            return false;
        }

        if (document.Definitions.Count == 0)
        {
            return false;
        }

        foreach (var definition in document.Definitions)
        {
            if (definition is not GraphQLOperationDefinition operation)
            {
                return false;
            }

            var allowed = operation.Operation switch
            {
                OperationType.Mutation => IsAdministrationMutation(operation.SelectionSet),
                OperationType.Query => IsOperationsRead(operation.SelectionSet),
                _ => false
            };
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAdministrationMutation(GraphQLSelectionSet root) =>
        AllFields(root, RuntimeFieldName, runtime =>
            AllFields(runtime.SelectionSet, MutationFields.ContainsKey, container =>
                AllFields(container.SelectionSet, MutationFields[container.Name.StringValue].Contains,
                    _ => true)));

    private static bool IsOperationsRead(GraphQLSelectionSet root) =>
        AllFields(root, RuntimeFieldName, runtime =>
            AllFields(runtime.SelectionSet, CkMethodOperationQueryField.FieldName, _ => true));

    private static bool AllFields(GraphQLSelectionSet? selectionSet, string name, Func<GraphQLField, bool> inner) =>
        AllFields(selectionSet, n => string.Equals(n, name, StringComparison.Ordinal), inner);

    private static bool AllFields(GraphQLSelectionSet? selectionSet, Func<string, bool> isAllowedName,
        Func<GraphQLField, bool> inner)
    {
        if (selectionSet is null || selectionSet.Selections.Count == 0)
        {
            return false;
        }

        foreach (var selection in selectionSet.Selections)
        {
            // a fragment spread or an inline fragment at these levels is refused: only plain fields are classified
            if (selection is not GraphQLField field || !isAllowedName(field.Name.StringValue) || !inner(field))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, HashSet<string>> BuildMutationFields()
    {
        var fields = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var qualified in CkParentTenantAdministrationMethods.MethodIds)
        {
            if (!CkMethodIds.TryParse(qualified, out var declaringType, out var methodId))
            {
                throw new InvalidOperationException($"The method id '{qualified}' is not a qualified method id.");
            }

            // the names RtMutation and CkMethodMutationFields give the type container and the method field
            var container = $"{declaringType.GetGraphQlCamelCaseName()}s";
            if (!fields.TryGetValue(container, out var methods))
            {
                methods = new HashSet<string>(StringComparer.Ordinal);
                fields[container] = methods;
            }

            methods.Add(CkMethodMutationFields.FieldName(methodId));
        }

        return fields;
    }

    private static bool IsJson(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        var mediaType = contentType.Split(';', 2)[0].Trim();
        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(mediaType, "application/graphql-response+json", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        request.EnableBuffering();
        try
        {
            var buffer = new byte[MaxBodyBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length &&
                   (read = await request.Body.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
            }

            return total > MaxBodyBytes ? null : buffer.AsSpan(0, total).ToArray();
        }
        finally
        {
            request.Body.Position = 0;
        }
    }

    private static bool TryReadQuery(byte[] body, out string query)
    {
        query = string.Empty;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false; // a batch (array) or anything else
            }

            foreach (var property in json.RootElement.EnumerateObject())
            {
                if (!AllowedBodyProperties.Contains(property.Name))
                {
                    return false;
                }

                if (property.Name == "extensions" &&
                    property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) &&
                    (property.Value.ValueKind != JsonValueKind.Object || property.Value.EnumerateObject().Any()))
                {
                    return false;
                }
            }

            if (!json.RootElement.TryGetProperty("query", out var queryElement) ||
                queryElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            query = queryElement.GetString() ?? string.Empty;
            return query.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
