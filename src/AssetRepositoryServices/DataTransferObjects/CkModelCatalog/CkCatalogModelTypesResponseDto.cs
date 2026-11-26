namespace Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;

/// <summary>
///     The content of a catalog CK model version - its types with attributes and associations, the
///     association roles and enums they use - read from the catalog without importing the model into a tenant.
///     One page of the types (sorted by type id) matching the optional filter.
/// </summary>
public class CkCatalogModelTypesResponseDto
{
    /// <summary>Name of the catalog the model was read from.</summary>
    public string CatalogName { get; set; } = string.Empty;

    /// <summary>Full model id with version, e.g. "Basic-2.7.0".</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Description of the model.</summary>
    public string? Description { get; set; }

    /// <summary>Models this model depends on (full ids or version ranges).</summary>
    public List<string> Dependencies { get; set; } = [];

    /// <summary>Number of types of the model that match the filter (all pages).</summary>
    public int TotalCount { get; set; }

    /// <summary>Number of types of the model (without filter).</summary>
    public int ModelTypeCount { get; set; }

    /// <summary>Skip of this page.</summary>
    public int Skip { get; set; }

    /// <summary>Take of this page (after the cap).</summary>
    public int Take { get; set; }

    /// <summary>The types of this page.</summary>
    public List<CkCatalogTypeDto> Types { get; set; } = [];

    /// <summary>The association roles of the model that the types of this page use.</summary>
    public List<CkCatalogAssociationRoleDto> AssociationRoles { get; set; } = [];

    /// <summary>The enums of the model that the attributes of this page use.</summary>
    public List<CkCatalogEnumDto> Enums { get; set; } = [];
}

/// <summary>A type of a catalog model.</summary>
public class CkCatalogTypeDto
{
    /// <summary>Type id, e.g. "Basic/Equipment".</summary>
    public string TypeId { get; set; } = string.Empty;

    /// <summary>Type it derives from in the runtime form "Model/Element", e.g. "System/Entity", if any.</summary>
    public string? DerivedFrom { get; set; }

    /// <summary>True for an abstract type (no instances).</summary>
    public bool IsAbstract { get; set; }

    /// <summary>True for a final type (cannot be derived from).</summary>
    public bool IsFinal { get; set; }

    /// <summary>Description of the type.</summary>
    public string? Description { get; set; }

    /// <summary>Attributes the type declares itself (inherited ones are on the base type).</summary>
    public List<CkCatalogTypeAttributeDto> Attributes { get; set; } = [];

    /// <summary>Outbound associations the type declares.</summary>
    public List<CkCatalogTypeAssociationDto> Associations { get; set; } = [];
}

/// <summary>An attribute of a catalog type.</summary>
public class CkCatalogTypeAttributeDto
{
    /// <summary>Attribute name on the type.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Attribute id in the runtime form "Model/Element" (model without version), e.g. "Basic/Name".</summary>
    public string AttributeId { get; set; } = string.Empty;

    /// <summary>Value type (e.g. String, Int, Enum, Record), null when the attribute comes from another model.</summary>
    public string? ValueType { get; set; }

    /// <summary>Enum id for enum attributes.</summary>
    public string? EnumId { get; set; }

    /// <summary>Record id for record attributes.</summary>
    public string? RecordId { get; set; }

    /// <summary>True when the attribute may be empty.</summary>
    public bool IsOptional { get; set; }

    /// <summary>Description of the attribute, if the model holds it.</summary>
    public string? Description { get; set; }
}

/// <summary>An outbound association of a catalog type.</summary>
public class CkCatalogTypeAssociationDto
{
    /// <summary>Association role id.</summary>
    public string RoleId { get; set; } = string.Empty;

    /// <summary>Target type id (or the target interface id).</summary>
    public string Target { get; set; } = string.Empty;
}

/// <summary>An association role of a catalog model.</summary>
public class CkCatalogAssociationRoleDto
{
    /// <summary>Association role id.</summary>
    public string RoleId { get; set; } = string.Empty;

    /// <summary>Name seen from the target.</summary>
    public string InboundName { get; set; } = string.Empty;

    /// <summary>Name seen from the source.</summary>
    public string OutboundName { get; set; } = string.Empty;

    /// <summary>Multiplicity seen from the target.</summary>
    public string InboundMultiplicity { get; set; } = string.Empty;

    /// <summary>Multiplicity seen from the source.</summary>
    public string OutboundMultiplicity { get; set; } = string.Empty;

    /// <summary>Description of the role.</summary>
    public string? Description { get; set; }
}

/// <summary>An enum of a catalog model.</summary>
public class CkCatalogEnumDto
{
    /// <summary>Enum id.</summary>
    public string EnumId { get; set; } = string.Empty;

    /// <summary>Values (key and name).</summary>
    public List<CkCatalogEnumValueDto> Values { get; set; } = [];
}

/// <summary>A value of a catalog enum.</summary>
public class CkCatalogEnumValueDto
{
    /// <summary>Numeric key.</summary>
    public int Key { get; set; }

    /// <summary>Name.</summary>
    public string Name { get; set; } = string.Empty;
}
