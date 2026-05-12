using GraphQL.Types;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;

/// <summary>
/// GraphQL input for the <c>createTimeRangeArchive</c> mutation. Concept-time-range §10.
/// Mirrors the rollup-create input shape: a thin DTO that carries the rollup-specific fields
/// the operator picks in the UI; the inherited Archive attributes (Status, etc.) are filled
/// server-side. Unlike rollups, TargetCkTypeId and Columns are picked by the operator directly
/// (no source archive to inherit them from).
/// </summary>
internal sealed class CreateTimeRangeArchiveInputType : InputObjectGraphType<CreateTimeRangeArchiveInputDto>
{
    public CreateTimeRangeArchiveInputType()
    {
        Name = "CreateTimeRangeArchiveInput";
        Description = "Input for createTimeRangeArchive: target CK type, columns, optional name + advisory period.";

        Field<StringGraphType>("rtWellKnownName")
            .Description("Optional human-readable name for the archive.");

        Field<NonNullGraphType<StringGraphType>>("targetCkTypeId")
            .Description("CK type id whose rows this archive captures windowed values for.");

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<ArchiveColumnSpecInputType>>>>("columns")
            .Description("Attribute paths to materialise as CrateDB columns. At least one required.");

        Field<IntGraphType>("periodMs")
            .Description("Advisory window length in milliseconds (e.g. 900000 = 15 min). Optional; descriptive only.");
    }
}
