using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services;

/// <summary>
///     Maps a compiled catalog CK model to one bounded page of its types (with attributes and associations)
///     and the association roles and enums that page uses. Pure function, nothing is written anywhere.
/// </summary>
internal static class CkCatalogModelTypesMapper
{
    /// <summary>Page size when the caller gives none.</summary>
    public const int DefaultTake = 20;

    /// <summary>Largest page size; a larger take is capped, so the response stays bounded.</summary>
    public const int MaxTake = 50;

    /// <summary>Longest description returned per element; longer ones are cut with an ellipsis.</summary>
    public const int MaxDescriptionLength = 300;

    /// <summary>Builds the page.</summary>
    /// <param name="catalogName">Catalog the model was read from.</param>
    /// <param name="model">The compiled model.</param>
    /// <param name="filter">
    ///     Optional case-insensitive text; a type matches when its id, description or one of its attribute names
    ///     contains it.
    /// </param>
    /// <param name="skip">Types to skip (negative counts as 0).</param>
    /// <param name="take">Page size (1..<see cref="MaxTake" />, values outside are clamped; 0 or less = default).</param>
    public static CkCatalogModelTypesResponseDto Map(string catalogName, CkCompiledModelRoot model, string? filter,
        int skip, int take)
    {
        skip = Math.Max(0, skip);
        take = take <= 0 ? DefaultTake : Math.Min(take, MaxTake);

        var modelName = model.ModelId.Name;
        var attributes = (model.Attributes ?? [])
            .GroupBy(a => a.AttributeId.SemanticVersionedFullName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var roles = (model.AssociationRoles ?? [])
            .GroupBy(r => r.AssociationRoleId.SemanticVersionedFullName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var enums = (model.Enums ?? [])
            .GroupBy(e => e.EnumId.SemanticVersionedFullName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // The filter sees the full description of the model, not the copy cut for the answer.
        var allTypes = (model.Types ?? [])
            .Select(t => (Source: t, Dto: MapType(model.ModelId, t, attributes, modelName)))
            .OrderBy(t => t.Dto.TypeId, StringComparer.Ordinal)
            .ToList();

        var matching = string.IsNullOrWhiteSpace(filter)
            ? allTypes.Select(t => t.Dto).ToList()
            : allTypes.Where(t => Matches(t.Dto, t.Source.Description, filter.Trim())).Select(t => t.Dto).ToList();

        var page = matching.Skip(skip).Take(take).ToList();

        var usedRoles = page.SelectMany(t => t.Associations)
            .Select(a => a.RoleId)
            .Distinct(StringComparer.Ordinal)
            .Select(id => roles.TryGetValue(LocalPart(id, modelName) ?? "", out var role) ? MapRole(id, role) : null)
            .Where(r => r != null)
            .Select(r => r!)
            .OrderBy(r => r.RoleId, StringComparer.Ordinal)
            .ToList();

        var usedEnums = page.SelectMany(t => t.Attributes)
            .Where(a => a.EnumId != null)
            .Select(a => a.EnumId!)
            .Distinct(StringComparer.Ordinal)
            .Select(id => enums.TryGetValue(LocalPart(id, modelName) ?? "", out var e) ? MapEnum(id, e) : null)
            .Where(e => e != null)
            .Select(e => e!)
            .OrderBy(e => e.EnumId, StringComparer.Ordinal)
            .ToList();

        return new CkCatalogModelTypesResponseDto
        {
            CatalogName = catalogName,
            ModelId = model.ModelId.FullName,
            Description = Cut(model.Description),
            Dependencies = model.DependencyRanges?.Select(d => d.Range.ToString()).ToList()
                           ?? model.Dependencies?.Select(d => d.FullName).ToList()
                           ?? [],
            TotalCount = matching.Count,
            ModelTypeCount = allTypes.Count,
            Skip = skip,
            Take = take,
            Types = page,
            AssociationRoles = usedRoles,
            Enums = usedEnums
        };
    }

    private static bool Matches(CkCatalogTypeDto type, string? fullDescription, string filter) =>
        type.TypeId.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (fullDescription?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || type.Attributes.Any(a => a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private static CkCatalogTypeDto MapType(CkModelId modelId, CkCompiledTypeDto type,
        IReadOnlyDictionary<string, CkAttributeDto> attributes, string modelName)
    {
        return new CkCatalogTypeDto
        {
            TypeId = RuntimeId(new CkId<CkTypeId>(modelId, type.TypeId)),
            DerivedFrom = type.DerivedFromCkTypeId == null ? null : RuntimeId(type.DerivedFromCkTypeId),
            IsAbstract = type.IsAbstract,
            IsFinal = type.IsFinal,
            Description = Cut(type.Description),
            Attributes = (type.Attributes ?? []).Select(a => MapAttribute(a, attributes, modelName)).ToList(),
            Associations = (type.Associations ?? []).Select(a => new CkCatalogTypeAssociationDto
            {
                RoleId = RuntimeId(a.CkRoleId),
                Target = a.TargetCkInterfaceId != null ? RuntimeId(a.TargetCkInterfaceId) : RuntimeId(a.TargetCkTypeId)
            }).ToList()
        };
    }

    private static CkCatalogTypeAttributeDto MapAttribute(CkTypeAttributeDto typeAttribute,
        IReadOnlyDictionary<string, CkAttributeDto> attributes, string modelName)
    {
        var result = new CkCatalogTypeAttributeDto
        {
            Name = typeAttribute.AttributeName,
            AttributeId = RuntimeId(typeAttribute.CkAttributeId),
            IsOptional = typeAttribute.IsOptional
        };

        // Only attributes defined in this model carry their definition; one from a dependency stays without value type.
        if (typeAttribute.CkAttributeId.ModelId.Name == modelName
            && attributes.TryGetValue(typeAttribute.CkAttributeId.ElementId.SemanticVersionedFullName, out var definition))
        {
            result.ValueType = definition.ValueType.ToString();
            result.EnumId = definition.ValueCkEnumId == null ? null : RuntimeId(definition.ValueCkEnumId);
            result.RecordId = definition.ValueCkRecordId == null ? null : RuntimeId(definition.ValueCkRecordId);
            result.Description = Cut(definition.Description);
        }

        return result;
    }

    private static CkCatalogAssociationRoleDto MapRole(string id, CkAssociationRoleDto role) => new()
    {
        RoleId = id,
        InboundName = role.InboundName,
        OutboundName = role.OutboundName,
        InboundMultiplicity = role.InboundMultiplicity.ToString(),
        OutboundMultiplicity = role.OutboundMultiplicity.ToString(),
        Description = Cut(role.Description)
    };

    private static CkCatalogEnumDto MapEnum(string id, CkEnumDto e) => new()
    {
        EnumId = id,
        Values = e.Values.Select(v => new CkCatalogEnumValueDto { Key = v.Key, Name = v.Name }).ToList()
    };

    /// <summary>
    ///     The element part of a semantic id ("Basic/Location" -> "Location") when the id belongs to
    ///     <paramref name="modelName" />, else null (an element of another model is not in this model's lists).
    /// </summary>
    private static string? LocalPart(string semanticId, string modelName)
    {
        var slash = semanticId.IndexOf('/');
        if (slash <= 0 || !string.Equals(semanticId[..slash], modelName, StringComparison.Ordinal))
        {
            return null;
        }

        return semanticId[(slash + 1)..];
    }

    /// <summary>
    ///     The id as tenant tools and queries name it: model name without version, then the element with its own version
    ///     suffix when above 1 (e.g. "Basic/Equipment", "System/ParentChild").
    /// </summary>
    private static string RuntimeId<TElementId>(CkId<TElementId> id) where TElementId : IComparable<TElementId>, ICkElementId =>
        $"{id.ModelId.Name}/{id.ElementId.SemanticVersionedFullName}";

    private static string? Cut(string? text) =>
        text == null || text.Length <= MaxDescriptionLength ? text : text[..(MaxDescriptionLength - 1)] + "…";
}
