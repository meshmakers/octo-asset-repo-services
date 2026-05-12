using GraphQL.Types;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

/// <summary>
/// GraphQL input for a single archive column inside <see cref="CreateTimeRangeArchiveInputType"/>.
/// Mirrors <c>CkArchiveColumnSpec</c> from Runtime.Contracts on the wire side.
/// </summary>
internal sealed class ArchiveColumnSpecInputType : InputObjectGraphType<ArchiveColumnSpecInputDto>
{
    public ArchiveColumnSpecInputType()
    {
        Name = "ArchiveColumnSpecInput";
        Description = "Attribute path to capture as a CrateDB column on the archive table, plus index/required flags.";

        Field<NonNullGraphType<StringGraphType>>("path")
            .Description("Attribute path on the target CK type (e.g. 'energyConsumed', 'sensor.reading.value').");

        Field<NonNullGraphType<BooleanGraphType>>("indexed")
            .Description("When true (default), CrateDB indexes the column by its standard rules. False emits INDEX OFF.");

        Field<NonNullGraphType<BooleanGraphType>>("required")
            .Description("When true, every insert must supply a non-null value for this path.");
    }
}
