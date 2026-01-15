using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

/// <summary>
///     Input type for generic association modification
/// </summary>
internal sealed class RtEntityAssociationGenericInputType : InputObjectGraphType<RtEntityAssociationGenericDto>
{
    public RtEntityAssociationGenericInputType()
    {
        Name = $"RtEntityAssociation{Statics.GraphQlInputSuffix}";
        Description = "Input for modifying associations on a runtime entity";

        Field(x => x.RoleName, typeof(NonNullGraphType<StringGraphType>))
            .Description("Name of the association role or navigation property");
        Field(x => x.Targets, typeof(NonNullGraphType<ListGraphType<RtAssociationInputDtoType>>))
            .Description("List of target entities to associate");
    }
}

/// <summary>
///     DTO for generic association input
/// </summary>
public class RtEntityAssociationGenericDto
{
    /// <summary>
    ///     Gets or sets the name of the association role or navigation property
    /// </summary>
    public string RoleName { get; set; } = null!;

    /// <summary>
    ///     Gets or sets the list of target associations
    /// </summary>
    public IList<RtAssociationInputDto>? Targets { get; set; }
}
