using System.Linq.Expressions;
using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

/// <summary>
///     Implements a GraphQL runtime entity type
/// </summary>
[DoNotRegister]
internal sealed class RtEntityDtoInputType : InputObjectGraphType<RtEntityDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="ckTypeId">Corresponding construction kit id</param>
    public RtEntityDtoInputType(RtCkId<CkTypeId> ckTypeId)
    {
        CkTypeId = ckTypeId;
        Name = $"{ckTypeId.GetGraphQlPascalCaseName()}{Statics.GraphQlInputSuffix}";

        Field(x => x.RtWellKnownName, true);
    }

    /// <summary>
    ///     Returns the construction kit id
    /// </summary>
    public RtCkId<CkTypeId> CkTypeId { get; }

    /// <inheritdoc />
    /// <remarks>We need an overload, to deserialize all properties to the dictionary of <see cref="RtEntityDto" /></remarks>
    public override object ParseDictionary(IDictionary<string, object?> value)
    {
        var rtEntity = value.ToObjectWithWithUnknownProperties<RtEntityDto>(out var unmappedDictionary);

        if (unmappedDictionary.Count > 0)
        {
            rtEntity.Attributes = new List<RtEntityAttributeDto>();
            foreach (var (dictKey, dictValue) in unmappedDictionary)
            {
                rtEntity.Attributes.Add(new RtEntityAttributeDto
                {
                    AttributeName = dictKey,
                    Value = dictValue
                });
            }
        }

        return rtEntity;
    }

    /// <summary>
    ///     Populates the type with ck related attributes and associations
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="graphTypesCache"></param>
    /// <param name="typeGraph">The cache item</param>
    /// <param name="options"></param>
    /// <param name="ckCacheService"></param>
    public void Populate(IOptions<OctoAssetRepositoryServicesOptions> options, ICkCacheService ckCacheService,
        string tenantId, IGraphTypesCache graphTypesCache,
        CkTypeGraph typeGraph)
    {
        var builder = OctoBuilder<RtEntityDto>.Create(this, options);
        foreach (var attribute in typeGraph.AllAttributes.Values)
        {
            builder.Attribute(graphTypesCache, attribute, true);
        }

        foreach (var ckTypeAssociationGraph in typeGraph.Associations.Out.All.GroupBy(x => x.NavigationPropertyName))
        {
            var allowedTypes = ckTypeAssociationGraph.SelectMany(x =>
                ckCacheService.GetCkType(tenantId, x.TargetCkTypeId).GetAllDerivedTypes(true));
            if (!allowedTypes.Any())
            {
                continue; // All Ck entities are abstract for that assocs
            }

            AddAssociation(ckTypeAssociationGraph.Key);
        }

        foreach (var ckTypeAssociationGraph in typeGraph.Associations.In.All.GroupBy(x => x.NavigationPropertyName))
        {
            var allowedTypes = ckTypeAssociationGraph.SelectMany(x =>
                ckCacheService.GetCkType(tenantId, x.OriginCkTypeId).GetAllDerivedTypes(true));
            if (!allowedTypes.Any())
            {
                continue; // All Ck entities are abstract for that assocs
            }

            AddAssociation(ckTypeAssociationGraph.Key);
        }
    }

    private void AddAssociation(string name)
    {
        Expression<Func<RtEntityDto, object?>> scalarValueExpression =
            dto => dto.Attributes!.First(x => x.AttributeName == name).Value;

        Field(name, type: typeof(ListGraphType<RtAssociationInputDtoType>), expression: scalarValueExpression);
    }
}