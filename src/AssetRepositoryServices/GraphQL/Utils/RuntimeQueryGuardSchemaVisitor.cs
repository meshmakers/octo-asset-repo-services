using GraphQL;
using GraphQL.Resolvers;
using GraphQL.Types;
using GraphQL.Utilities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

/// <summary>
///     Puts the <see cref="RuntimeQueryGuard" /> (AB#8898) in front of the resolvers of the runtime-entity query entry
///     points when a tenant schema is initialized: <c>runtime.runtimeEntities</c>, every generated
///     <c>runtime.&lt;CkType&gt;</c> connection, <c>RtQuery.rows</c> (persisted queries) and <c>RtTransientQuery.rows</c>
///     (transient queries). Only these fields are wrapped, so every other field resolves without overhead, and the guard
///     runs before the resolver, i.e. before the entities or rows are read.
/// </summary>
/// <remarks>
///     <c>runtime.byInterface</c> is bounded by its required <c>take</c> (at most 1000, AB#6932) and is not wrapped.
///     Aggregation connections (<c>RtQuery.aggregations</c>, <c>RtTransientQuery.aggregations</c>) are not wrapped:
///     aggregation queries are never unbounded. Navigation connections inside an entity are not covered by AB#8898.
/// </remarks>
internal sealed class RuntimeQueryGuardSchemaVisitor(RuntimeQueryGuard guard) : BaseSchemaNodeVisitor
{
    /// <summary>The GraphQL name of the runtime root type (<see cref="RuntimeModelQuery" />).</summary>
    internal const string RuntimeTypeName = "RuntimeModelQuery";

    /// <summary>The GraphQL name of the persisted query type (<c>RtQueryDtoType</c>).</summary>
    internal const string PersistedQueryTypeName = "RtQuery";

    /// <summary>The GraphQL name of the transient query type (<c>RtTransientQueryDtoType</c>).</summary>
    internal const string TransientQueryTypeName = "RtTransientQuery";

    private const string RuntimeEntitiesFieldName = "runtimeEntities";
    private const string RowsFieldName = "rows";

    /// <inheritdoc />
    public override void VisitObjectFieldDefinition(FieldType field, IObjectGraphType type, ISchema schema)
    {
        var entryPoint = GetEntryPoint(field, type);
        if (entryPoint == null || field.Resolver == null || field.Resolver is GuardedResolver)
        {
            return;
        }

        field.Resolver = new GuardedResolver(field.Resolver, guard, entryPoint.Value);
    }

    /// <summary>The entry point a field is, or null when the guard does not watch it.</summary>
    internal static RuntimeQueryEntryPoint? GetEntryPoint(FieldType field, IGraphType type)
    {
        if (string.Equals(type.Name, RuntimeTypeName, StringComparison.Ordinal))
        {
            if (string.Equals(field.Name, RuntimeEntitiesFieldName, StringComparison.OrdinalIgnoreCase))
            {
                return RuntimeQueryEntryPoint.GenericEntities;
            }

            // The generated per-CK-type connections carry the CK type id as metadata (RuntimeModelQuery).
            return field.HasMetadata(Statics.CkId) ? RuntimeQueryEntryPoint.TypedEntities : null;
        }

        if (!string.Equals(field.Name, RowsFieldName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return type.Name switch
        {
            PersistedQueryTypeName => RuntimeQueryEntryPoint.PersistedQueryRows,
            TransientQueryTypeName => RuntimeQueryEntryPoint.TransientQueryRows,
            _ => null
        };
    }

    private sealed class GuardedResolver(IFieldResolver inner, RuntimeQueryGuard guard, RuntimeQueryEntryPoint entryPoint)
        : IFieldResolver
    {
        public ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            guard.Check(context, entryPoint);
            return inner.ResolveAsync(context);
        }
    }
}
