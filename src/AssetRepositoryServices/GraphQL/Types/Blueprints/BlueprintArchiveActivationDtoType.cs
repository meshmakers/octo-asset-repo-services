using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>GraphQL projection of <see cref="BlueprintArchiveActivationDto" /> (AB#7991).</summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BlueprintArchiveActivationDtoType : ObjectGraphType<BlueprintArchiveActivationDto>
{
    public BlueprintArchiveActivationDtoType()
    {
        Name = "BlueprintArchiveActivation";
        Description = "An archive of a blueprint seed and what the install or update did with it (AB#7991).";

        Field<NonNullGraphType<StringGraphType>>("archiveRtId").Description("Runtime id of the archive.")
            .Resolve(ctx => ctx.Source!.ArchiveRtId);
        Field<StringGraphType>("wellKnownName").Description("Well-known name of the archive, if it has one.")
            .Resolve(ctx => ctx.Source!.WellKnownName);
        Field<NonNullGraphType<StringGraphType>>("ckTypeId").Description("CK type of the archive entity.")
            .Resolve(ctx => ctx.Source!.CkTypeId);
        Field<NonNullGraphType<StringGraphType>>("blueprintId").Description("The blueprint whose seed delivered the archive.")
            .Resolve(ctx => ctx.Source!.BlueprintId);
        Field<NonNullGraphType<BooleanGraphType>>("isRollup").Description("True when the archive is a rollup.")
            .Resolve(ctx => ctx.Source!.IsRollup);
        Field<NonNullGraphType<StringGraphType>>("outcome")
            .Description("Activated, SkippedPreExisting, SkippedOptOut, SkippedStreamDataDisabled or Failed.")
            .Resolve(ctx => ctx.Source!.Outcome);
        Field<StringGraphType>("reason").Description("Why the archive was skipped or failed; null when it was activated.")
            .Resolve(ctx => ctx.Source!.Reason);
        Field<NonNullGraphType<StringGraphType>>("backfill")
            .Description("NotApplicable, Queued, NothingToBackfill, NotQueued or Failed.")
            .Resolve(ctx => ctx.Source!.Backfill);
        Field<StringGraphType>("backfillJobRtId").Description("The queued backfill job of a new rollup.")
            .Resolve(ctx => ctx.Source!.BackfillJobRtId);
        Field<StringGraphType>("backfillReason").Description("Why no backfill was queued or why queueing failed.")
            .Resolve(ctx => ctx.Source!.BackfillReason);
    }
}
