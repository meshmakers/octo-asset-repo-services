using System.Linq.Expressions;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

/// <summary>
///     Implements a GraphQL runtime entity type
/// </summary>
public sealed class RtEntityDtoInputType : InputObjectGraphType<RtEntityDto>
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="ckTypeId">Corresponding construction kit id</param>
    public RtEntityDtoInputType(CkId<CkTypeId> ckTypeId)
    {
        CkTypeId = ckTypeId;
        Name = $"{ckTypeId.GetGraphQlName()}{CommonConstants.GraphQlInputSuffix}";

        Field(x => x.RtWellKnownName, true);
    }

    /// <summary>
    ///     Returns the construction kit id
    /// </summary>
    public CkId<CkTypeId> CkTypeId { get; }

    /// <inheritdoc />
    /// <remarks>We need an overload, to deserialize all properties to the dictionary of <see cref="RtEntityDto" /></remarks>
    public override object ParseDictionary(IDictionary<string, object?> value)
    {
        return value.ToObjectWithWithUnknownProperties<RtEntityDto>() ?? throw new InvalidOperationException();
    }

    /// <summary>
    ///     Populates the type with ck related attributes and associations
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="graphTypesCache"></param>
    /// <param name="typeGraph">The cache item</param>
    /// <param name="ckCacheService"></param>
    public void Populate(ICkCacheService ckCacheService, string tenantId, IGraphTypesCache graphTypesCache, CkTypeGraph typeGraph)
    {
        foreach (var attribute in typeGraph.AllAttributes.Values)
        {
            Helpers.AddAttribute(this, graphTypesCache, attribute, true);
        }

        foreach (var ckTypeAssociationGraph in typeGraph.Associations.Out.All)
        {
            var allowedTypes = ckCacheService.GetCkType(tenantId, ckTypeAssociationGraph.TargetCkTypeId).DerivedTypes;
            if (!allowedTypes.Any())
            {
                continue; // All Ck entities are abstract for that assocs
            }

            AddAssociation(ckTypeAssociationGraph.NavigationPropertyName);
        }

        foreach (var ckTypeAssociationGraph in typeGraph.Associations.In.All)
        {
            var allowedTypes = ckCacheService.GetCkType(tenantId, ckTypeAssociationGraph.TargetCkTypeId).DerivedTypes;
            if (!allowedTypes.Any())
            {
                continue; // All Ck entities are abstract for that assocs
            }

            AddAssociation(ckTypeAssociationGraph.NavigationPropertyName);
        }
    }

    private void AddAssociation(string name)
    {
        Expression<Func<RtEntityDto, object>> scalarValueExpression = dto => dto.Properties![name];

        Field(name, type: typeof(ListGraphType<RtAssociationInputDtoType>), expression: scalarValueExpression);
    }
}