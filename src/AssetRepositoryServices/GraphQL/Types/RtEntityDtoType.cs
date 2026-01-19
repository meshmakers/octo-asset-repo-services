using AssetRepositoryServices.Resources;
using GraphQL;
using GraphQL.Builders;
using GraphQL.DataLoader;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Implements the GraphQL Runtime Entity Type
/// </summary>
[DoNotRegister]
internal sealed class RtEntityDtoType : ObjectGraphType<RtEntityDto>
{
    private readonly CkTypeGraph _ckTypeGraph;

    /// <inheritdoc />
    public RtEntityDtoType(CkTypeGraph ckTypeGraph)
    {
        _ckTypeGraph = ckTypeGraph;

        Name = _ckTypeGraph.CkTypeId.ToRtCkId().GetGraphQlPascalCaseName();
        Description = $"Runtime entities of construction kit type '{_ckTypeGraph.CkTypeId}'";
        IsTypeOf = o =>
        {
            if (o is RtEntityDto rtEntityDto)
            {
                return _ckTypeGraph.GetAllDerivedTypes(true).Select(t => t.ToRtCkId()).Contains(rtEntityDto.CkTypeId);
            }

            return false;
        };

        Field(d => d.RtId, typeof(NonNullGraphType<OctoObjectIdType>));
        Field(d => d.CkTypeId, typeof(NonNullGraphType<RtCkIdGraph<CkTypeId>>));
        Field(d => d.RtCreationDateTime, typeof(DateTimeGraphType));
        Field(d => d.RtChangedDateTime, typeof(DateTimeGraphType));
        Field(x => x.RtWellKnownName, true);
        Field(x => x.RtVersion, true);
    }

    /// <summary>
    ///     Returns the Construction Kid ID of the object type
    /// </summary>
    public CkId<CkTypeId> CkTypeId => _ckTypeGraph.CkTypeId;

    /// <summary>
    ///     Returns true if the type is abstract
    /// </summary>
    public bool IsAbstract => _ckTypeGraph.IsAbstract;

    internal void Populate(IOptions<OctoAssetRepositoryServicesOptions> options, ICkCacheService ckCacheService,
        string tenantId, IGraphTypesCache graphTypesCache)
    {
        AddConstructionKit();
        AddGenericAssociations();

        var builder = OctoBuilder<RtEntityDto>.Create(this, options);
        foreach (var attribute in _ckTypeGraph.AllAttributes.Values)
        {
            builder.Attribute(graphTypesCache, attribute, false);
        }

        // Get implemented interfaces - we'll add them after association fields are created
        // This enables fragment inheritance where a fragment on a base type matches derived types
        var implementedInterfaces = graphTypesCache.GetImplementedInterfaces(CkTypeId.ToRtCkId());

        foreach (var ckTypeAssociationGraph in _ckTypeGraph.Associations.Out.All.GroupBy(x => x.NavigationPropertyName))
        {
            // Get all derived types but filter out abstract types since they can't have instances
            // The union should only contain concrete types that can actually be returned
            var allowedTypes = ckTypeAssociationGraph
                .SelectMany(x => ckCacheService.GetCkType(tenantId, x.TargetCkTypeId).GetAllDerivedTypes(true))
                .Where(x => !ckCacheService.GetCkType(tenantId, x).IsAbstract)
                .Select(x => x.ToRtCkId())
                .Distinct()
                .ToList();
            if (!allowedTypes.Any())
            {
                continue; // All Ck types are abstract for that association
            }

            // The query base type is the target type where the association points to
            // (may be abstract, but repository will query for all derived types)
            var queryBaseType = ckTypeAssociationGraph.First().TargetCkTypeId.ToRtCkId();

            this.AssociationField(graphTypesCache, ckTypeAssociationGraph.Key,
                    allowedTypes, _ckTypeGraph.CkTypeId.ToRtCkId(), queryBaseType,
                    ckTypeAssociationGraph.First().CkRoleId.ToRtCkId(), GraphDirections.Outbound)
                .Argument<StringGraphType>(Statics.CkTypeIdArg, "Filter by specific CK type ID (can be a base type to include all derived types). If not specified, returns all allowed types.")
                .Argument<ListGraphType<StringGraphType>>(Statics.CkTypeIdsArg, "Filter by multiple CK type IDs (can include base types to include all derived types).")
                .Argument<OctoObjectIdType>(Statics.RtIdArg, "Returns the entity with the given rtId.")
                .Argument<ListGraphType<OctoObjectIdType>>(Statics.RtIdsArg, "Returns entities with the given rtIds.")
                .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
                .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
                .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg, "Filters items based on field compare")
                .Argument<ResultAggregationInputDtoType>(Statics.AggregationsArg, AssetTexts.Graphql_Type_Filter_Aggregations_Description)
                .Resolve(ResolveAssociationQuery);
        }

        foreach (var ckTypeAssociationGraph in _ckTypeGraph.Associations.In.All.GroupBy(x => x.NavigationPropertyName))
        {
            // Get all derived types but filter out abstract types since they can't have instances
            // The union should only contain concrete types that can actually be returned
            var allowedTypes = ckTypeAssociationGraph
                .SelectMany(x => ckCacheService.GetCkType(tenantId, x.OriginCkTypeId).GetAllDerivedTypes(true))
                .Where(x => !ckCacheService.GetCkType(tenantId, x).IsAbstract)
                .Select(x => x.ToRtCkId())
                .Distinct()
                .ToList();
            if (!allowedTypes.Any())
            {
                continue; // All Ck types are abstract for that association
            }

            // The query base type is the origin type where the association was defined
            // (may be abstract like Vehicle, but repository will query for all derived types)
            var queryBaseType = ckTypeAssociationGraph.First().OriginCkTypeId.ToRtCkId();

            this.AssociationField(graphTypesCache, ckTypeAssociationGraph.Key,
                    allowedTypes, _ckTypeGraph.CkTypeId.ToRtCkId(), queryBaseType,
                    ckTypeAssociationGraph.First().CkRoleId.ToRtCkId(), GraphDirections.Inbound)
                .Argument<StringGraphType>(Statics.CkTypeIdArg, "Filter by specific CK type ID (can be a base type to include all derived types). If not specified, returns all allowed types.")
                .Argument<ListGraphType<StringGraphType>>(Statics.CkTypeIdsArg, "Filter by multiple CK type IDs (can include base types to include all derived types).")
                .Argument<OctoObjectIdType>(Statics.RtIdArg, "Returns the entity with the given rtId.")
                .Argument<ListGraphType<OctoObjectIdType>>(Statics.RtIdsArg, "Returns entities with the given rtIds.")
                .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
                .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
                .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg, "Filters items based on field compare")
                .Argument<ResultAggregationInputDtoType>(Statics.AggregationsArg, AssetTexts.Graphql_Type_Filter_Aggregations_Description)
                .Resolve(ResolveAssociationQuery);
        }

        // Now that all fields are added, implement interfaces from abstract parent types
        // Only add interfaces if we have all required fields with compatible types.
        //
        // Important considerations:
        // 1. When an association is INHERITED (same definition at all levels), all interfaces
        //    should have the same field type, and we use the first interface's type as canonical.
        //
        // 2. When an association is OVERRIDDEN at a lower level (e.g., WideSource.LinksTo -> WideTarget,
        //    but NarrowSource.LinksTo -> NarrowTarget), the interfaces at different levels will have
        //    DIFFERENT field types. In this case, we can only implement the interface that matches
        //    our actual field type.
        //
        // For the override scenario (EnergyIQ RelatesFrom issue):
        // - WideSourceInterface has LinkedFrom field with type WideTarget_LinkedFromUnionConnection
        // - NarrowSourceInterface has LinkedFrom field with type NarrowTarget_LinkedFromUnionConnection
        // - ConcreteSource (inherits from NarrowSource) has LinkedFrom with NarrowTarget type
        // - ConcreteSource can implement NarrowSourceInterface but NOT WideSourceInterface
        //   because the field types are incompatible

        foreach (var interfaceType in implementedInterfaces)
        {
            // Check if we can implement this interface:
            // 1. We must have all required fields
            // 2. Field types must be compatible (same type or we can adopt the interface's type)
            var canImplementInterface = true;
            var fieldsToUpdate = new List<(FieldType ourField, IGraphType interfaceType)>();

            foreach (var interfaceField in interfaceType.Fields)
            {
                var ourField = Fields.FirstOrDefault(f => f.Name == interfaceField.Name);
                if (ourField == null)
                {
                    // We're missing this field, can't implement the interface
                    canImplementInterface = false;
                    break;
                }

                // Check if field types are compatible
                if (interfaceField.ResolvedType != null && ourField.ResolvedType != null)
                {
                    // Get the underlying type names (unwrap NonNull if present)
                    var interfaceTypeName = GetUnderlyingTypeName(interfaceField.ResolvedType);
                    var ourTypeName = GetUnderlyingTypeName(ourField.ResolvedType);

                    if (interfaceTypeName != ourTypeName)
                    {
                        // The types are different. This happens when an association is overridden
                        // at a lower level in the hierarchy. We cannot implement this interface
                        // because our field type is incompatible.
                        canImplementInterface = false;
                        break;
                    }
                }

                // If our field doesn't have a resolved type yet but the interface does,
                // we can adopt the interface's type
                if (ourField.ResolvedType == null && interfaceField.ResolvedType != null)
                {
                    fieldsToUpdate.Add((ourField, interfaceField.ResolvedType));
                }
            }

            if (!canImplementInterface)
            {
                continue;
            }

            // Update field types from the interface (for fields that didn't have a type yet)
            foreach (var (ourField, resolvedType) in fieldsToUpdate)
            {
                ourField.ResolvedType = resolvedType;
            }

            AddResolvedInterface(interfaceType);
        }
    }

    private object? ResolveAssociationQuery(IResolveConnectionContext<RtEntityDto> ctx)
    {
        try
        {
            var sessionAccessor = ctx.GetSessionAccessor();
            var graphQlUserContext = (GraphQlUserContext)ctx.UserContext;

            var offset = ctx.GetOffset();
            var queryOptions = ctx.GetQueryOptions();

            // Get metadata from field
            var originCkId = (RtCkId<CkTypeId>)ctx.FieldDefinition.Metadata[Statics.OriginCkId]!;
            var roleId = (RtCkId<CkAssociationRoleId>)ctx.FieldDefinition.Metadata[Statics.RoleId]!;
            var graphDirection = (GraphDirections)ctx.FieldDefinition.Metadata[Statics.GraphDirection]!;
            var allowedTypes = (IReadOnlyList<RtCkId<CkTypeId>>)ctx.FieldDefinition.Metadata[Statics.AllowedTypes]!;
            var queryBaseType = (RtCkId<CkTypeId>)ctx.FieldDefinition.Metadata[Statics.QueryBaseType]!;

            // Get optional ckTypeId and ckTypeIds filter arguments
            ctx.TryGetArgument(Statics.CkTypeIdArg, out string? ckTypeIdFilter);
            ctx.TryGetArgument(Statics.CkTypeIdsArg, null, out IEnumerable<string>? ckTypeIdsFilter);
            var ckTypeIdsList = ckTypeIdsFilter?.ToList();

            // Combine single and list filters
            var requestedTypeIds = new List<string>();
            if (!string.IsNullOrEmpty(ckTypeIdFilter))
            {
                requestedTypeIds.Add(ckTypeIdFilter);
            }
            if (ckTypeIdsList != null)
            {
                requestedTypeIds.AddRange(ckTypeIdsList);
            }

            // Get the CK cache service for type validation
            var ckCacheService = ctx.GetCkCacheService();

            // Determine target CK type - use filter if provided, otherwise use the base type
            // to query for all derived types
            RtCkId<CkTypeId>? targetCkId = null;
            if (requestedTypeIds.Count > 0)
            {
                // Validate all requested types
                foreach (var requestedTypeId in requestedTypeIds)
                {
                    var parsedCkId = new RtCkId<CkTypeId>(requestedTypeId);

                    // Check if the type is directly in the allowed list (concrete type)
                    if (allowedTypes.Contains(parsedCkId))
                    {
                        continue; // Valid concrete type
                    }

                    // Check if the type is a base type whose derived types are in allowedTypes
                    // This allows filtering by abstract types like "System.Communication/Pipeline"
                    try
                    {
                        var ckTypeGraph = ckCacheService.GetRtCkType(graphQlUserContext.TenantId, parsedCkId);
                        var derivedTypes = ckTypeGraph.GetAllDerivedTypes(false)
                            .Select(t => t.ToRtCkId())
                            .ToList();

                        var hasAllowedDerivedType = derivedTypes.Any(derivedType => allowedTypes.Contains(derivedType));
                        if (!hasAllowedDerivedType)
                        {
                            var allowedTypeNames = allowedTypes.Select(t => t.SemanticVersionedFullName).ToArray();
                            throw new ArgumentException($"Type '{requestedTypeId}' has no derived types that are allowed for this association. Allowed types: {string.Join(", ", allowedTypeNames)}");
                        }
                    }
                    catch (KeyNotFoundException)
                    {
                        throw new ArgumentException($"Type '{requestedTypeId}' does not exist.");
                    }
                }

                // If single type, use it directly (repository will query all derived types)
                // If multiple types, expand each to concrete types and filter (if needed)
                if (requestedTypeIds.Count == 1)
                {
                    targetCkId = new RtCkId<CkTypeId>(requestedTypeIds[0]);
                }
                else
                {
                    // For multiple types, we need to:
                    // 1. Use the query base type to get all potential targets
                    // 2. Add a field filter to restrict to only the requested types (if not all allowed)
                    targetCkId = queryBaseType;

                    // Expand each requested type to its concrete derived types
                    // (base types like "Vehicle" need to be expanded to ["Car", "Truck"])
                    var concreteTypeIds = new HashSet<RtCkId<CkTypeId>>();
                    foreach (var requestedTypeId in requestedTypeIds)
                    {
                        var parsedCkId = new RtCkId<CkTypeId>(requestedTypeId);
                        var ckTypeGraph = ckCacheService.GetRtCkType(graphQlUserContext.TenantId, parsedCkId);

                        // Get all derived types (including self if it's concrete)
                        var derivedTypes = ckTypeGraph.GetAllDerivedTypes(true);
                        foreach (var derivedType in derivedTypes)
                        {
                            var derivedCkTypeGraph = ckCacheService.GetCkType(graphQlUserContext.TenantId, derivedType);
                            // Only include concrete (non-abstract) types that are in the allowed list
                            if (!derivedCkTypeGraph.IsAbstract)
                            {
                                var rtCkId = derivedType.ToRtCkId();
                                if (allowedTypes.Contains(rtCkId))
                                {
                                    concreteTypeIds.Add(rtCkId);
                                }
                            }
                        }
                    }

                    // Only add filter if the requested types are a subset of allowed types
                    // If all allowed types are covered, no filter is needed
                    if (concreteTypeIds.Count > 0 && concreteTypeIds.Count < allowedTypes.Count)
                    {
                        // Use the RtCkId objects directly for the filter - the repository will serialize correctly
                        var ckTypeIdValues = concreteTypeIds.ToArray();
                        queryOptions = queryOptions.FieldFilter("ckTypeId", FieldFilterOperator.In, ckTypeIdValues);
                    }
                }
            }

            // Get optional rtId filters
            ctx.TryGetArgument(Statics.RtIdArg, out OctoObjectId? key);
            ctx.TryGetArgument(Statics.RtIdsArg, null, out IEnumerable<OctoObjectId>? keys);
            var keysList = keys?.ToList();
            if (key != null)
            {
                keysList ??= new List<OctoObjectId>();
                keysList.Add(key.Value);
            }

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();

            // If no specific type filter, use the base type (may be abstract) so the repository
            // will query for all derived types. This ensures associations to derived types like
            // Car and Truck are included when querying via the abstract Vehicle type.
            var queryTargetCkId = targetCkId ?? queryBaseType;

            // Get DataLoader context
            var dataLoaderAccessor = ctx.RequestServices?.GetRequiredService<IDataLoaderContextAccessor>();
            if (dataLoaderAccessor?.Context == null)
            {
                throw AssetRepositoryException.DataLoaderContextUnavailable();
            }

            // Create a unique cache key for this batch loader based on all query parameters
            // This ensures different filter combinations use separate batch loaders
            var cacheKey = $"Assoc_{originCkId}_{queryTargetCkId}_{roleId}_{graphDirection}_{offset}_{ctx.First}_{queryOptions.GetHashCode()}_{string.Join(",", keysList?.Select(k => k.ToString()) ?? [])}";

            // Use DataLoader to batch association queries for multiple entities
            var loader = dataLoaderAccessor.Context.GetOrAddBatchLoader<RtEntityId, IResultSet<RtEntity>>(
                cacheKey,
                async rtEntityIds =>
                    await tenantRepository.GetRtAssociationTargetsAsync(
                        sessionAccessor.Session,
                        rtEntityIds.Select(x => x.RtId),
                        originCkId,
                        roleId,
                        queryTargetCkId,
                        graphDirection,
                        keysList,
                        queryOptions,
                        offset,
                        ctx.First));

            var dataLoaderResult = loader.LoadAsync(ctx.Source.ToRtEntityId());

            return dataLoaderResult.Then(resultSet => ConnectionUtils.ToOctoConnection(
                resultSet.Items.Select(CreateRtEntityDto),
                ctx,
                resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0,
                (int)resultSet.TotalCount));
        }
        catch (Exception e)
        {
            return ctx.HandleException(e);
        }
    }

    private void AddConstructionKit()
    {
        Field<CkTypeDtoType>("ConstructionKitType")
            .Resolve(ResolveCkType);
    }

    private void AddGenericAssociations()
    {
        Connection<RtEntityGenericDtoType>("Associations")
            .Argument<NonNullGraphType<StringGraphType>>(Statics.RoleIdArg, "The role id of the association.")
            .Argument<BooleanGraphType>(Statics.IncludeIndirectArg,
                "Include indirect associations, otherwise direct associations are returned.")
            .Argument<NonNullGraphType<GraphDirectionsDtoType>>(Statics.DirectionArg,
                "The direction of the association.")
            .Argument<NonNullGraphType<StringGraphType>>(Statics.CkIdArg,
                "The construction kit type with the given id.")
            .Argument<SearchFilterDtoType>(Statics.SearchFilterArg, "Filters items based on text search")
            .Argument<ResultAggregationInputDtoType>(Statics.AggregationsArg,
                AssetTexts.Graphql_Type_Filter_Aggregations_Description)
            .Argument<ListGraphType<SortDtoType>>(Statics.SortOrderArg, "Sort order for items")
            .Argument<ListGraphType<FieldFilterDtoType>>(Statics.FieldFilterArg,
                "Filters items based on field compare")
            .Resolve(ResolveGenericRtAssociationsQuery);
    }

    private object? ResolveGenericRtAssociationsQuery(IResolveConnectionContext<RtEntityDto> arg)
    {
        try
        {
            var sessionAccessor = arg.GetSessionAccessor();
            var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

            var offset = arg.GetOffset();
            var queryOptions = arg.GetQueryOptions();

            if (!arg.TryGetArgument(Statics.IncludeIndirectArg, out bool? indirectAssociations))
            {
                indirectAssociations = false;
            }

            var roleId = arg.GetArgument<RtCkId<CkAssociationRoleId>>(Statics.RoleIdArg);
            var direction = arg.GetArgument<GraphDirections>(Statics.DirectionArg);
            var targetCkId = arg.GetArgument<RtCkId<CkTypeId>>(Statics.CkId);

            var tenantRepository = graphQlUserContext.TenantContext.GetTenantRepository();

            // Get DataLoader context
            var dataLoaderAccessor = arg.RequestServices?.GetRequiredService<IDataLoaderContextAccessor>();
            if (dataLoaderAccessor?.Context == null)
            {
                throw AssetRepositoryException.DataLoaderContextUnavailable();
            }

            var originCkId = CkTypeId.ToRtCkId();

            if (indirectAssociations.Value)
            {
                // Create a unique cache key for indirect associations
                var cacheKey = $"GenericAssocIndirect_{originCkId}_{targetCkId}_{roleId}_{direction}_{offset}_{arg.First}_{queryOptions.GetHashCode()}";

                var loader = dataLoaderAccessor.Context.GetOrAddBatchLoader<RtEntityId, IResultSet<RtEntity>>(
                    cacheKey,
                    async rtEntityIds =>
                        await tenantRepository.GetIndirectRtAssociationTargetsAsync(
                            sessionAccessor.Session,
                            rtEntityIds.Select(x => x.RtId),
                            originCkId,
                            roleId,
                            direction,
                            null,
                            targetCkId,
                            queryOptions,
                            offset,
                            arg.First));

                var dataLoaderResult = loader.LoadAsync(arg.Source.ToRtEntityId());

                return dataLoaderResult.Then(resultSet => ConnectionUtils.ToOctoConnection(
                    resultSet.Items.Select(CreateRtEntityDto),
                    arg,
                    resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0,
                    (int)resultSet.TotalCount));
            }
            else
            {
                // Create a unique cache key for direct associations
                var cacheKey = $"GenericAssocDirect_{originCkId}_{targetCkId}_{roleId}_{direction}_{offset}_{arg.First}_{queryOptions.GetHashCode()}";

                var loader = dataLoaderAccessor.Context.GetOrAddBatchLoader<RtEntityId, IResultSet<RtEntity>>(
                    cacheKey,
                    async rtEntityIds =>
                        await tenantRepository.GetRtAssociationTargetsAsync(
                            sessionAccessor.Session,
                            rtEntityIds.Select(x => x.RtId),
                            originCkId,
                            roleId,
                            targetCkId,
                            direction,
                            null,
                            queryOptions,
                            offset,
                            arg.First));

                var dataLoaderResult = loader.LoadAsync(arg.Source.ToRtEntityId());

                return dataLoaderResult.Then(resultSet => ConnectionUtils.ToOctoConnection(
                    resultSet.Items.Select(CreateRtEntityDto),
                    arg,
                    resultSet.TotalCount > 0 ? offset.GetValueOrDefault(0) : 0,
                    (int)resultSet.TotalCount));
            }
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }

    private object ResolveCkType(IResolveFieldContext<RtEntityDto> arg)
    {
        var ckCacheService = arg.GetCkCacheService();
        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        var ckTypeGraph = ckCacheService.GetRtCkType(graphQlUserContext.TenantId, arg.Source.CkTypeId);
        return CkTypeDtoType.CreateCkTypeDto(ckTypeGraph);
    }

    internal static RtEntityDto CreateRtEntityDto(RtEntity rtEntity)
    {
        var rtEntityDto = new RtEntityDto
        {
            RtId = rtEntity.RtId,
            CkTypeId = rtEntity.CkTypeId ?? throw OctoGraphQLException.CkTypeIdUndefined(),
            RtCreationDateTime = rtEntity.RtCreationDateTime,
            RtChangedDateTime = rtEntity.RtChangedDateTime,
            RtWellKnownName = rtEntity.RtWellKnownName,
            RtVersion = rtEntity.RtVersion,
            UserContext = rtEntity
        };
        return rtEntityDto;
    }

    /// <summary>
    /// Gets the underlying type name, unwrapping NonNull and List wrappers.
    /// For example, NonNullGraphType&lt;ListGraphType&lt;MyType&gt;&gt; returns "MyType".
    /// </summary>
    private static string? GetUnderlyingTypeName(IGraphType graphType)
    {
        var current = graphType;
        while (current is IProvideResolvedType wrapper && wrapper.ResolvedType != null)
        {
            current = wrapper.ResolvedType;
        }

        return current?.Name;
    }
}