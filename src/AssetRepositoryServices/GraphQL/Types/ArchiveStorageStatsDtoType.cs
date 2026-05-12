using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// GraphQL projection of <see cref="ArchiveStorageStatsDto"/>. Returned by the
/// <c>archivesStorageStats</c> bulk query so the studio's archives list can render Records /
/// Size / Health columns in a single round-trip.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class ArchiveStorageStatsDtoType : ObjectGraphType<ArchiveStorageStatsDto>
{
    public ArchiveStorageStatsDtoType()
    {
        Name = "ArchiveStorageStats";
        Description = "Per-archive backend storage stats — row count, on-disk size, health classification. Backend-agnostic; the underlying CrateDB provider maps its native signals onto the Health enum so clients don't have to know about shards or replicas.";

        Field<NonNullGraphType<OctoObjectIdType>>("archiveRtId")
            .Description("Runtime id of the archive these stats describe.")
            .Resolve(ctx => ctx.Source!.ArchiveRtId);

        Field<NonNullGraphType<BooleanGraphType>>("tableExists")
            .Description("True when the backing storage table is provisioned (archive has been activated). False ⇒ RecordCount and SizeBytes are 0 and Health is Unknown.")
            .Resolve(ctx => ctx.Source!.TableExists);

        Field<NonNullGraphType<LongGraphType>>("recordCount")
            .Description("Total number of stored rows. Primary copies only — replicas are not double-counted.")
            .Resolve(ctx => ctx.Source!.RecordCount);

        Field<NonNullGraphType<LongGraphType>>("sizeBytes")
            .Description("On-disk size of the primary copies in bytes. UIs typically format this human-readable (KiB / MiB / GiB).")
            .Resolve(ctx => ctx.Source!.SizeBytes);

        Field<NonNullGraphType<ArchiveStorageHealthGraphType>>("health")
            .Description("Overall health classification. Unknown means the provider could not determine a state — render distinctly from Good.")
            .Resolve(ctx => ctx.Source!.Health);
    }
}

/// <summary>
/// GraphQL enum for <see cref="ArchiveStorageHealth"/>. Uses the C# names directly so the schema
/// reads as Good / Warning / Critical / Unknown.
/// </summary>
internal sealed class ArchiveStorageHealthGraphType : EnumerationGraphType<ArchiveStorageHealth>
{
    public ArchiveStorageHealthGraphType()
    {
        Name = "ArchiveStorageHealth";
        Description = "Backend-agnostic health classification: Unknown / Good / Warning / Critical.";
    }
}
