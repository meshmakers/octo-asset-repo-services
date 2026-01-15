using System.Linq.Expressions;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

/// <summary>
///     Implements a GraphQL runtime entity type for generic mutations
/// </summary>
internal sealed class RtEntityDtoGenericInputType : InputObjectGraphType<RtEntityDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public RtEntityDtoGenericInputType()
    {
        Name = $"RtEntity{Statics.GraphQlInputSuffix}";

        Field(x => x.CkTypeId, typeof(NonNullGraphType<RtCkIdGraph<CkTypeId>>));
        Field(x => x.RtWellKnownName, true);
        Field(x => x.Attributes, typeof(NonNullGraphType<ListGraphType<RtEntityAttributeDtoInputType>>));

        // Add associations field using expression that maps to Attributes to avoid automatic CLR property mapping
        // The actual parsing is handled in ParseDictionary
        Expression<Func<RtEntityDto, object?>> associationsExpression =
            dto => dto.Attributes!.FirstOrDefault(x => x.AttributeName == "associations")!.Value;
        Field("associations", type: typeof(ListGraphType<RtEntityAssociationGenericInputType>),
            expression: associationsExpression)
            .Description("Associations to create or modify on this entity");
    }

    /// <inheritdoc />
    /// <remarks>We need an overload to deserialize associations into the attributes list</remarks>
    public override object ParseDictionary(IDictionary<string, object?> value)
    {
        // Use ToObjectWithWithUnknownProperties to avoid CLR mapping issues
        // This is the same approach used by RtEntityDtoInputType
        var rtEntity = value.ToObjectWithWithUnknownProperties<RtEntityDto>(out var unmappedDictionary);

        // Convert Attributes to a List if it's an array (fixed size collection)
        if (rtEntity.Attributes is not null and not List<RtEntityAttributeDto>)
        {
            rtEntity.Attributes = new List<RtEntityAttributeDto>(rtEntity.Attributes);
        }

        // Process regular attributes from unmapped properties (excluding associations)
        if (unmappedDictionary.Count > 0)
        {
            rtEntity.Attributes ??= new List<RtEntityAttributeDto>();
            foreach (var (dictKey, dictValue) in unmappedDictionary)
            {
                // Skip associations - they are processed separately below
                if (dictKey.Equals("associations", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                rtEntity.Attributes.Add(new RtEntityAttributeDto
                {
                    AttributeName = dictKey,
                    Value = dictValue
                });
            }
        }

        // Process associations from input
        if (value.TryGetValue("associations", out var associationsValue) && associationsValue is IEnumerable<object> associations)
        {
            rtEntity.Attributes ??= new List<RtEntityAttributeDto>();

            foreach (var association in associations)
            {
                if (association is RtEntityAssociationGenericDto assocDto)
                {
                    rtEntity.Attributes.Add(new RtEntityAttributeDto
                    {
                        AttributeName = assocDto.RoleName,
                        Value = assocDto.Targets
                    });
                }
            }
        }

        return rtEntity;
    }
}