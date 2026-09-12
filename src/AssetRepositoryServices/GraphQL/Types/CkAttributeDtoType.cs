using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using CkAttributeDto = Meshmakers.Octo.Communication.Contracts.DataTransferObjects.CkAttributeDto;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Construction kit attributes Graph QL type definition
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class CkAttributeDtoType : ObjectGraphType<CkAttributeDto>
{
    /// <inheritdoc />
    public CkAttributeDtoType()
    {
        Name = "CkAttribute";
        Description = "Construction kit attribute definitions";

        Field(x => x.CkAttributeId, typeof(NonNullGraphType<CkIdGraph<CkAttributeId>>))
            .Description("Construction kit attribute id.");
        Field(x => x.AttributeValueType, typeof(NonNullGraphType<AttributeValueTypesDtoType>))
            .Description("Value type of the attribute.");
        Field<CkRecordDtoType>("CkRecord")
            .Description("Optional record id of the attribute value type.")
            .Resolve(ResolveCkRecord);
        Field<CkEnumDtoType>("CkEnum")
            .Description("Optional enum id of the attribute value type.")
            .Resolve(ResolveCkEnum);
        Field(x => x.Description, typeof(StringGraphType))
            .Description("Optional description of the attribute.");
        Field(x => x.MetaData, typeof(ListGraphType<CkAttributeMetaDataDtoType>))
            .Description("Optional meta data of the attribute.");
        Field<ListGraphType<SimpleScalarType>, object>(nameof(CkAttributeDto.DefaultValues))
            .Description("Default values of the attribute.");
        Field<NonNullGraphType<AttributeOwnershipDtoType>>("ownership")
            .Description("Who owns this attribute DEFINITION's value, and therefore what installing the blueprint " +
                         "again does to it (AB#5187): SEED_OWNED is overwritten by a re-apply and is exported; " +
                         "TENANT_OWNED keeps the tenant's value on a re-apply and is still exported; RUNTIME_STATE " +
                         "and SECRET keep the existing value on a re-apply and are excluded from an export. " +
                         "This is the default for every assignment of the attribute - an assignment on a type or " +
                         "record may override it, see CkTypeAttribute.ownershipOverride.")
            .Resolve(ctx => ResolveOwnership(ctx));
        // Deliberately NO isRuntimeState field (AB#5191): the boolean is the deprecated alias whose
        // conflation of "preserved on re-apply" with "excluded from export" is what AB#5187 set out to
        // end. Carrying it into a NEW interface would perpetuate exactly that ambiguity, and nothing
        // consumes this surface yet, so there is no compatibility argument for it. Should a caller ever
        // need a predicate rather than the enum, add a NAMED one (isPreservedOnReapply /
        // isExcludedFromExport) so it states which of the two it answers.
    }

    private static AttributeOwnershipDto ResolveOwnership(IResolveFieldContext<CkAttributeDto> arg)
    {
        if (arg.Source is OwnershipAwareCkAttributeDto ownershipAwareCkAttributeDto)
        {
            return ownershipAwareCkAttributeDto.Ownership;
        }

        // Never guess here: reporting SEED_OWNED for an attribute whose ownership is unknown would tell an
        // operator that a credential is safe to overwrite. Every construction site fills the value in.
        throw new InvalidOperationException(
            $"Ownership of construction kit attribute '{arg.Source.CkAttributeId}' was not resolved.");
    }

    private object? ResolveCkEnum(IResolveFieldContext<CkAttributeDto> arg)
    {
        var ckCacheService = arg.GetCkCacheService();
        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        if (arg.Source.ValueCkEnumId == null)
        {
            return null;
        }

        var ckEnumGraph = ckCacheService.GetCkEnum(graphQlUserContext.TenantId, arg.Source.ValueCkEnumId);
        return CkEnumDtoType.CreateCkEnumDto(ckEnumGraph);
    }

    private object? ResolveCkRecord(IResolveFieldContext<CkAttributeDto> arg)
    {
        var ckCacheService = arg.GetCkCacheService();
        var graphQlUserContext = (GraphQlUserContext)arg.UserContext;

        if (arg.Source.ValueCkRecordId == null)
        {
            return null;
        }

        var ckRecordGraph = ckCacheService.GetCkRecord(graphQlUserContext.TenantId, arg.Source.ValueCkRecordId);
        return CkRecordDtoType.CreateCkRecordDto(ckRecordGraph);
    }


    /// <param name="ckTypeAttributeGraph">The compiled attribute assignment the definition DTO is built from.</param>
    /// <param name="definitionOwnership">
    ///     AB#5191: resolved ownership of the attribute DEFINITION. It is passed in rather than read from
    ///     <see cref="CkTypeAttributeGraph.Ownership" />, which is the ASSIGNMENT's effective value and
    ///     therefore already reflects a per-assignment override.
    /// </param>
    internal static CkAttributeDto CreateCkAttributeDto(CkTypeAttributeGraph ckTypeAttributeGraph,
        AttributeOwnershipDto definitionOwnership)
    {
        var attributeDto = new OwnershipAwareCkAttributeDto
        {
            Ownership = definitionOwnership,
            CkAttributeId = ckTypeAttributeGraph.CkAttributeId,
            AttributeValueType = ckTypeAttributeGraph.ValueType,
            ValueCkRecordId = ckTypeAttributeGraph.ValueCkRecordId,
            ValueCkEnumId = ckTypeAttributeGraph.ValueCkEnumId,
            Description = ckTypeAttributeGraph.Description,
            MetaData = ckTypeAttributeGraph.MetaData?.Select(CkAttributeMetaDataDtoType.CreateCkAttributeMetaDataDto)
                .ToList(),
            DefaultValues = ckTypeAttributeGraph.DefaultValues
        };

        return attributeDto;
    }

    internal static CkAttributeDto CreateCkAttributeDto(CkAttribute ckAttribute)
    {
        var attributeDto = new OwnershipAwareCkAttributeDto
        {
            // AB#5191: a document written before AB#5187 has no ownership and must be read through the
            // deprecated isRuntimeState alias, which is exactly what AttributeOwnership.Resolve does.
            Ownership = AttributeOwnership.Resolve(ckAttribute.Ownership, ckAttribute.IsRuntimeState),
            CkAttributeId = ckAttribute.CkAttributeId,
            AttributeValueType = ckAttribute.AttributeValueType,
            ValueCkRecordId = ckAttribute.ValueCkRecordId,
            ValueCkEnumId = ckAttribute.ValueCkEnumId,
            Description = ckAttribute.Description,
            DefaultValues = ckAttribute.DefaultValues
        };

        return attributeDto;
    }
}