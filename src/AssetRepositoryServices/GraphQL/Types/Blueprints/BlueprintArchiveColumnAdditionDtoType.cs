using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>GraphQL projection of <see cref="BlueprintArchiveColumnAdditionDto" /> (AB#7997).</summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BlueprintArchiveColumnAdditionDtoType : ObjectGraphType<BlueprintArchiveColumnAdditionDto>
{
    public BlueprintArchiveColumnAdditionDtoType()
    {
        Name = "BlueprintArchiveColumnAddition";
        Description = "A column a blueprint update added to an activated archive (AB#7997).";

        Field<NonNullGraphType<StringGraphType>>("archiveRtId").Description("Runtime id of the archive.")
            .Resolve(ctx => ctx.Source!.ArchiveRtId);
        Field<StringGraphType>("archiveWellKnownName").Description("Well-known name of the archive, if it has one.")
            .Resolve(ctx => ctx.Source!.ArchiveWellKnownName);
        Field<NonNullGraphType<StringGraphType>>("column").Description("Attribute path of an ingested column, name of a computed column.")
            .Resolve(ctx => ctx.Source!.Column);
        Field<NonNullGraphType<BooleanGraphType>>("isComputed").Description("True for a computed column.")
            .Resolve(ctx => ctx.Source!.IsComputed);
        Field<StringGraphType>("backfillJobRtId").Description("The queued backfill job of a computed column.")
            .Resolve(ctx => ctx.Source!.BackfillJobRtId);
        Field<StringGraphType>("error").Description("Why the column could not be added; null when it was added.")
            .Resolve(ctx => ctx.Source!.Error);
    }
}
